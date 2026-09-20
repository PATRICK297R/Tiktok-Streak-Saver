using Microsoft.Maui.Controls.Shapes;
using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;

namespace TiktokStreakSaver.Pages;

/// <summary>
/// Reads the conversations in the user's TikTok inbox and offers to add the ones that are not
/// already in the list.
/// <para>
/// The reading itself is done by <c>tiktok_read_contacts.js</c>, which reports into
/// <c>window.__saverScan</c> and pushes every step through the <c>SaverScan</c> bridge. On Android the
/// script is driven by <c>ContactScanService</c> rather than by this page, so a long friend list can be
/// read with the app closed; this page starts it, watches its progress and then reviews the result.
/// </para>
/// <para>
/// Deciding what to add stays in C#, where it shares its matching rules with the backup restore - which
/// is why the scanner reports what it saw and this page decides what it means.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class ImportContactsPage : ContentPage
{
    private readonly SettingsService _settingsService;
    private readonly SessionService _sessionService;

    /// <summary>
    /// Watches the scan. The page does not do the scanning any more: on Android that is done by
    /// <c>ContactScanService</c>, which is what lets the app be closed while a long friend list is read.
    /// This page starts it, follows its progress and then reviews the result.
    /// </summary>
    private IDispatcherTimer? _pollTimer;

    private bool _isAdding;

    /// <summary>Set once the results are on screen, so the polling stops and nothing is drawn twice.</summary>
    private bool _resultsShown;

    /// <summary>So the "you can close the app" notice is given once per visit rather than on every press.</summary>
    private bool _toldAboutBackground;

    /// <summary>
    /// The scanned chats as the review list holds them: which to add, and what has been corrected.
    /// <para>
    /// The scan is a good guess and not an authority - a group whose name is one word reads as a person -
    /// so nothing is saved until this has been seen. Keeping it here rather than in the rows means a
    /// correction survives the row being redrawn, which is what makes the edit worth making.
    /// </para>
    /// </summary>
    private ImportDraft _draft = new(null);

    /// <summary>
    /// One redraw per row, so correcting an entry updates its own row instead of rebuilding the list and
    /// throwing away the place the user had scrolled to.
    /// </summary>
    private readonly List<Action> _rowRefreshers = new();

    /// <summary>
    /// How many of the snapshot's chats have already been drawn into the live preview.
    /// </summary>
    /// <remarks>
    /// Counted so each poll appends only what is new. Redrawing the whole preview on every tick would
    /// throw away and rebuild every row once a second, which flickers and grows more expensive the longer
    /// the list gets - and the list is longest exactly when the scan is nearly finished.
    /// </remarks>
    private int _previewRendered;

    /// <summary>How often the page looks for the service's progress. Far shorter than reading one chat takes.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A stored scan that says it is running but has not been written for this long is treated as dead.
    /// <para>
    /// Only reachable after the process was killed mid-scan: the service writes at least every couple of
    /// seconds while it works, and always when it stops. Without this, reopening the app after it was
    /// closed mid-scan would wait forever on a scan that no longer exists, because the only record of it
    /// still says "running".
    /// </para>
    /// </summary>
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(4);

    public ImportContactsPage()
    {
        InitializeComponent();
        _settingsService = new SettingsService();
        _sessionService = new SessionService();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Re-evaluated on every visit rather than once, because coming back from the sign-in page is exactly
        // the moment this has to look again.
        _resultsShown = false;
        ResultsPanel.IsVisible = false;
        OutcomePanel.IsVisible = false;
        ScanPanel.IsVisible = true;
        ScanDetailLabel.Text = string.Empty;
        ResetScanPreview();

        // The inbox needs a session; without one TikTok shows a login wall and the scan would report
        // "nothing found", which reads like a bug rather than a missing login.
        if (!_sessionService.IsSessionValid())
        {
            // Offered rather than enforced by closing the page. Being told to sign in and then having the
            // page close under you is a dead end: the sign-in it needs is one page away, and there is no way
            // back to the import from where you are left standing.
            ShowScanOutcome(Localization.T("Imp_SignInRequiredTitle"),
                Localization.T("Imp_SignInRequiredBody"),
                offerSignIn: true);
            return;
        }

#if ANDROID
        var snapshot = ContactScanHub.Store.Read();

        if (ContactScanSnapshot.IsAbandoned(snapshot, DateTime.Now, AbandonedAfter))
        {
            // The app was closed while the scan was running and the process did not survive it. Saying so
            // beats waiting on a scan that is no longer there.
            LogDiagnostic("previous import was abandoned — the app was closed while it was running");
            ContactScanHub.Store.Clear();
            snapshot = null;
        }

        // Results from a scan that already finished - including one that finished while the app was
        // closed, which is the normal case for a long list.
        if (snapshot is { IsStopped: true })
        {
            ShowSnapshotResults(snapshot);
            return;
        }

        if (snapshot is null)
        {
            StartServiceScan();
            ScanDetailLabel.Text = Localization.T("Imp_StartingReader");
        }

        StartPolling();
#else
        // The scanner reports through a JavaScript interface, which only exists on Android. There is no
        // equivalent on iOS, and Apple has nothing like a foreground service to keep a WebView reading
        // while the app is closed - which is the point of the feature. Saying so plainly is better than a
        // progress screen that can never move.
        ShowScanOutcome(Localization.T("Imp_IosTitle"),
            Localization.T("Imp_IosBody"),
            offerSignIn: false);
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // Only the watching stops. On Android the scan runs in a service and carries on without this page,
        // which is the whole reason it was moved there.
        StopPolling();
    }

    // ─── Driving the background scan ─────────────────────────────────────────

    /// <summary>
    /// Asks the background service to read the inbox.
    /// <para>
    /// The stored snapshot is cleared first, because it is what this page reads: the previous scan's
    /// results would otherwise be shown as if they were this one's.
    /// </para>
    /// </summary>
    private async void StartServiceScan()
    {
#if ANDROID
        try
        {
            ContactScanHub.Store.Clear();

            var context = Platform.CurrentActivity ?? Android.App.Application.Context;
            var intent = new Android.Content.Intent(context, typeof(Platforms.Android.Services.ContactScanService));

            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
                context.StartForegroundService(intent);
            else
                context.StartService(intent);

            LogDiagnostic("import started from the import page");
        }
        catch (Exception ex)
        {
            // Worth surfacing rather than only logging: an import that cannot start looks exactly like one
            // that is running slowly.
            LogDiagnostic($"import could not be started: {ex.Message}");

            ScanActivity.IsRunning = false;
            ScanStatusLabel.Text = Localization.T("Imp_CouldNotStart");
            StopScanButton.IsVisible = false;

            await DisplayAlert(Localization.T("Imp_UnavailableTitle"),
                string.Format(Localization.T("Imp_UnavailableBody"), ex.Message),
                Localization.T("Common_OK"));
        }
#else
        await Task.CompletedTask;
#endif
    }

    private void StartPolling()
    {
        if (_pollTimer is null)
        {
            _pollTimer = Dispatcher.CreateTimer();
            _pollTimer.Interval = PollInterval;
            _pollTimer.Tick += OnPollTick;
        }

        _pollTimer.Start();
    }

    private void StopPolling()
    {
        if (_pollTimer is null)
            return;

        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTick;
        _pollTimer = null;
    }

    /// <summary>
    /// Follows the scan by reading the snapshot the service publishes.
    /// <para>
    /// Polling rather than being called back: the service and this page are in the same process, but the
    /// scan is also allowed to finish while the app is closed, so its state has to be readable at any time
    /// rather than only pushed at moments this page happens to be alive for.
    /// </para>
    /// </summary>
    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_resultsShown || _isAdding)
            return;

        var snapshot = ContactScanHub.Store.Read();
        if (snapshot is null)
            return;

        if (snapshot.IsStopped)
        {
            ShowSnapshotResults(snapshot);
            return;
        }

        // The service can be killed without ever getting the chance to publish a stopping state. Without
        // this the page would sit on a spinner that can never move again.
        if (ContactScanSnapshot.IsAbandoned(snapshot, DateTime.Now, AbandonedAfter))
        {
            LogDiagnostic("import was abandoned while the page was watching it");
            ContactScanHub.Store.Clear();

            // Offered as a panel rather than an alert, so the retry it needs is one tap away instead of
            // being a message about a problem with no way to act on it.
            ShowScanOutcome(Localization.T("Imp_StoppedTitle"),
                Localization.T("Imp_StoppedBody") + " " + Localization.T("Imp_StoppedHint"),
                offerSignIn: false);
            return;
        }

        var text = ContactScanSnapshot.ProgressText(snapshot);
        if (text != ScanStatusLabel.Text)
            ScanStatusLabel.Text = text;

        var detail = DescribeScanProgress(snapshot);
        if (detail != ScanDetailLabel.Text)
            ScanDetailLabel.Text = detail;

        AppendScanPreview(snapshot.Items);
    }

    /// <summary>
    /// Adds any chats read since the last poll to the live preview, so each one appears as it is read
    /// rather than the whole list arriving at the end.
    /// </summary>
    /// <remarks>
    /// Only appends. The rows carry no gestures and no controls, so there is nothing for the user to press
    /// that the next poll would undo; editing is offered by the review list, which is built once the scan
    /// has stopped and the list has stopped changing.
    /// </remarks>
    private void AppendScanPreview(IReadOnlyList<ContactScanItemDto>? items)
    {
        if (items is null || items.Count <= _previewRendered)
            return;

        for (var i = _previewRendered; i < items.Count; i++)
            ScanPreviewList.Children.Add(CreatePreviewRow(items[i]));

        _previewRendered = items.Count;
        ScanPreviewSection.IsVisible = true;
    }

    /// <summary>Empties the preview, for the start of a scan and for going back to the scan panel.</summary>
    private void ResetScanPreview()
    {
        ScanPreviewList.Children.Clear();
        _previewRendered = 0;
        ScanPreviewSection.IsVisible = false;
    }

    /// <summary>
    /// One chat in the preview: a read marker, the name, and the handle or the word Group.
    /// <para>
    /// Deliberately not a <c>CreateResultRow</c>: that one carries a tick box and an Edit button, and a
    /// row that looks editable but does nothing is worse than one that plainly does not.
    /// </para>
    /// </summary>
    private static View CreatePreviewRow(ContactScanItemDto item)
    {
        var name = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Username : item.DisplayName;
        var subtitle = item.IsGroup
            ? Localization.T("Common_Group")
            : (string.IsNullOrWhiteSpace(item.Username) ? Localization.T("Imp_NoHandleRead") : $"@{item.Username}");

        var row = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(12, 10),
        };
        row.SetAppThemeColor(Border.BackgroundColorProperty,
            ThemeColor("ListItemLight", "#F5F5F7"), ThemeColor("ListItemDark", "#252629"));
        row.SetAppThemeColor(Border.StrokeProperty,
            ThemeColor("ListItemBorderLight", "#EBEDF0"), ThemeColor("ListItemBorderDark", "#2E3036"));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star },
            },
            ColumnSpacing = 10,
        };

        // A tick rather than a spinner: by the time a chat reaches this list it has been read and judged,
        // and this is what says so without inviting a press.
        var tick = new Label
        {
            Text = "✓",
            FontSize = 14,
            FontFamily = "InterSemiBold",
            VerticalOptions = LayoutOptions.Center,
        };
        tick.SetAppThemeColor(Label.TextColorProperty,
            ThemeColor("Success", "#22946E"), ThemeColor("SuccessDark", "#47D5A6"));
        grid.Children.Add(tick);

        var stack = new VerticalStackLayout { Spacing = 2 };
        var nameLabel = new Label
        {
            Text = name,
            FontSize = 15,
            FontFamily = "InterMedium",
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
        };
        nameLabel.SetAppThemeColor(Label.TextColorProperty,
            ThemeColor("Gray950", "#141517"), ThemeColor("White", "#FFFFFF"));
        stack.Children.Add(nameLabel);

        var subtitleLabel = new Label
        {
            Text = subtitle,
            FontSize = 13,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
        };
        subtitleLabel.SetAppThemeColor(Label.TextColorProperty,
            ThemeColor("Gray400", "#8B8F96"), ThemeColor("Gray400", "#8B8F96"));
        stack.Children.Add(subtitleLabel);

        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        row.Content = grid;
        return row;
    }

    /// <summary>Reads a colour token, falling back to a literal so a missing key cannot throw mid-scan.</summary>
    private static Color ThemeColor(string key, string fallbackHex)
    {
        if (Application.Current is { } app && app.Resources.TryGetValue(key, out var value) && value is Color color)
            return color;
        return Color.FromArgb(fallbackHex);
    }

    /// <summary>
    /// The second line under the status: how far along the scan is, and the reminder that closing the app
    /// is safe. The status line already says what is happening, so this only adds what it cannot.
    /// </summary>
    private static string DescribeScanProgress(ContactScanSnapshot snapshot)
    {
        var read = snapshot.Items.Count;
        var checkedCount = snapshot.Attempts;

        // Nothing opened yet: the page is still loading, or the first chat is being read.
        if (checkedCount <= 0)
            return Localization.T("Imp_ReadingSlow");

        return read == 1
            ? string.Format(Localization.T("Imp_ProgressOne"), read, checkedCount)
            : string.Format(Localization.T("Imp_ProgressMany"), read, checkedCount);
    }

    /// <summary>
    /// Asks the running scan to stop. What was read before stopping is kept and offered, because the user
    /// asked for their chats and a partial list is more use than none.
    /// </summary>
    private void OnStopScanClicked(object? sender, EventArgs e)
    {
        StopScanButton.IsEnabled = false;
        ScanStatusLabel.Text = Localization.T("Imp_Stopping");

#if ANDROID
        try
        {
            var context = Platform.CurrentActivity ?? Android.App.Application.Context;
            var intent = new Android.Content.Intent(context, typeof(Platforms.Android.Services.ContactScanService));
            intent.SetAction(AppConstants.ActionStopContactScan);

            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
        }
        catch (Exception ex)
        {
            LogDiagnostic($"import could not be stopped: {ex.Message}");
        }
#endif

        // The service publishes the stopped snapshot; the poll picks it up and shows whatever was read.
    }

    /// <summary>
    /// Leaves the page without stopping the scan, which is the point of the whole arrangement: a long
    /// friend list can be left to finish with the app closed.
    /// </summary>
    private async void OnCloseScanClicked(object? sender, EventArgs e)
    {
        if (!_toldAboutBackground && !_resultsShown)
        {
            _toldAboutBackground = true;
            await DisplayAlert(Localization.T("Imp_StillReadingTitle"),
                Localization.T("Imp_StillBody") + " " + Localization.T("Imp_StillHint"),
                Localization.T("Common_OK"));
        }

        await Navigation.PopAsync();
    }

    /// <summary>
    /// Records a line in the app's run log, which the History page can export.
    /// <para>
    /// Added because the importer was, for several rounds, the only part of the app with no evidence
    /// trail: when it failed, nothing on the device could say why. Anything the user can export is
    /// worth more than another guess.
    /// </para>
    /// </summary>
    private static void LogDiagnostic(string message)
    {
        try
        {
#if ANDROID
            // Recorded through the service's own log path, so the line lands in the in-memory log as
            // well as the file and therefore shows up in the export. Writing only to the file would
            // hide it whenever a run had already filled the in-memory log.
            Platforms.Android.Services.StreakService.AppendExternalLog("IMPORT", "-", message);
#else
            var path = System.IO.Path.Combine(FileSystem.AppDataDirectory, AppConstants.RunLogsFileName);
            new RunLogStore(path).Append("IMPORT", "-", message, DateTime.Now);
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Import diagnostic log failed: {ex.Message}");
        }
    }

    // ─── Results ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows what the scan read, once it has stopped for any reason.
    /// <para>
    /// Taken from the stored snapshot rather than from a live report, so a scan that finished while the app
    /// was closed is shown exactly like one that finished with the page open.
    /// </para>
    /// <para>
    /// When there is nothing to offer, the reason and a way forward are shown instead - see
    /// <see cref="ShowScanOutcome"/>.
    /// </para>
    /// </summary>
    private void ShowSnapshotResults(ContactScanSnapshot snapshot)
    {
        if (_resultsShown)
            return;

        _resultsShown = true;
        StopPolling();

        ScanActivity.IsRunning = false;
        ScanStatusLabel.Text = ContactScanSnapshot.ProgressText(snapshot);
        StopScanButton.IsVisible = false;
        CloseScanButton.IsVisible = false;

        var scanned = ContactScanReader.ToFriends(snapshot.Items);

        // Only what is not on the list already. An entry the user has had for months is not something to
        // confirm again, and reviewing it would bury the ones that are actually new.
        var found = ContactScanReader.SelectNew(_settingsService.GetFriendsList(), scanned);
        var alreadyKnown = scanned.Count - found.Count;

        if (scanned.Count == 0)
        {
            // Kept on the page with a way forward instead of closing on a message. Closing was the dead end:
            // the scan had just failed, the reason usually points at something the user has to fix, and the
            // only place to fix it from was the page that had just disappeared.
            var reason = snapshot.Phase == ContactScanPhase.Failed
                ? ContactScanSnapshot.SummaryText(snapshot)
                : ContactScanReader.DescribeEmptyScan(snapshot.ToScanDto());

            ShowScanOutcome(
                snapshot.Phase == ContactScanPhase.Failed ? Localization.T("Imp_FailedTitle") : Localization.T("Imp_NothingReadTitle"),
                reason,
                // A rejected session is much the commonest reason an import fails and it is the one the user
                // can do something about, so the way to it is offered rather than described. It costs nothing
                // when the session turns out to have been fine.
                offerSignIn: snapshot.Phase == ContactScanPhase.Failed);
            return;
        }

        if (found.Count == 0)
        {
            // A success with nothing to do, which still needs a way out that is not "close and start again".
            ShowScanOutcome(Localization.T("Imp_NothingToAddTitle"),
                scanned.Count == 1
                    ? string.Format(Localization.T("Imp_AllKnownOne"), scanned.Count)
                    : string.Format(Localization.T("Imp_AllKnownMany"), scanned.Count),
                offerSignIn: false);
            return;
        }

        _draft = new ImportDraft(found);

        ResultsDetailLabel.Text = string.Format(Localization.T("Scan_ReadConversations"), scanned.Count) + " "
            + (alreadyKnown > 0 ? string.Format(Localization.T("Imp_ResultsKnown"), alreadyKnown) + " " : string.Empty)
            + Localization.T("Imp_ResultsTickHint");

        // A scan that stopped early has to say so, or a short list reads as a complete one.
        ResultsWarningLabel.Text = snapshot.Message;
        ResultsWarningLabel.IsVisible = snapshot.Message.Length > 0;

        ShowRows();

        OutcomePanel.IsVisible = false;
        ScanPanel.IsVisible = false;
        ResultsPanel.IsVisible = true;
    }

    /// <summary>
    /// Shows a scan that produced nothing usable, with the reason and a way forward.
    /// <para>
    /// Every one of these states used to close the page on an alert. That left the user with a message about
    /// something they had to fix - sign in, check the connection - and no way to act on it from where they
    /// were standing, so the only option was to go and find the import again. The page stays, and Try again
    /// is always there.
    /// </para>
    /// </summary>
    private void ShowScanOutcome(string title, string reason, bool offerSignIn)
    {
        _resultsShown = true;
        StopPolling();

        ScanActivity.IsRunning = false;
        ScanStatusLabel.Text = title;
        StopScanButton.IsVisible = false;
        CloseScanButton.IsVisible = false;

        ResultsPanel.IsVisible = false;
        ScanPanel.IsVisible = false;

        OutcomeTitleLabel.Text = title;
        OutcomeReasonLabel.Text = reason;
        SignInButton.IsVisible = offerSignIn;
        OutcomePanel.IsVisible = true;
    }

    /// <summary>
    /// Opens the sign-in page, and comes back here when it closes.
    /// <para>
    /// Returned to on purpose: the import is what the user was doing, and the page re-reads the session when
    /// it appears, so signing in leads straight back to a scan rather than to the Profile page they would
    /// then have to navigate out of.
    /// </para>
    /// </summary>
    private async void OnSignInClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new LoginPage());
    }

    /// <summary>
    /// Starts a fresh scan, forgetting whatever is on screen.
    /// <para>
    /// Used by both "Scan again" and "Try again": they are the same action from the user's point of view, and
    /// having two would only be two things to keep working.
    /// </para>
    /// </summary>
    private void OnScanAgainClicked(object? sender, EventArgs e)
    {
        _resultsShown = false;
        _draft = new ImportDraft(null);
        ShowRows();
        ResetScanPreview();

        ResultsPanel.IsVisible = false;
        OutcomePanel.IsVisible = false;
        ScanPanel.IsVisible = true;

        ScanActivity.IsRunning = true;
        StopScanButton.IsVisible = true;
        StopScanButton.IsEnabled = true;
        CloseScanButton.IsVisible = true;
        ScanStatusLabel.Text = Localization.T("Imp_Opening");
        ScanDetailLabel.Text = string.Empty;

        StartServiceScan();
        StartPolling();
    }

    /// <summary>Draws the review list from the draft, and refreshes what depends on the selection.</summary>
    private void ShowRows()
    {
        _rowRefreshers.Clear();

        var rowsToRemove = ResultsList.Children.ToList();
        foreach (var row in rowsToRemove) ResultsList.Children.Remove(row);

        foreach (var entry in _draft.Entries)
            ResultsList.Children.Add(CreateResultRow(entry));

        RefreshDraftSummary();
    }

    /// <summary>
    /// Puts the selection on screen: how much is ticked, what is wrong with any of it, and whether there
    /// is anything to add.
    /// <para>
    /// The Add button is disabled rather than hidden when a ticked entry is not ready, so the count and
    /// the reason stay on screen together instead of the button silently doing nothing.
    /// </para>
    /// </summary>
    private void RefreshDraftSummary()
    {
        ResultsSummaryLabel.Text = _draft.DescribeSelection();

        var blocked = _draft.DescribeBlocked();
        ResultsBlockedLabel.Text = blocked ?? string.Empty;
        ResultsBlockedLabel.IsVisible = blocked is not null;

        var toAdd = _draft.ToAdd().Count;
        AddContactsButton.Text = toAdd == 1 ? Localization.T("Imp_AddOne") : string.Format(Localization.T("Imp_AddMany"), toAdd);
        AddContactsButton.IsEnabled = toAdd > 0 && !_isAdding;
    }

    private View CreateResultRow(ImportDraftEntry entry)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(22) },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 4
        };

        var check = new CheckBox { IsChecked = entry.Include, VerticalOptions = LayoutOptions.Center };
        grid.Children.Add(check);

        var marker = new Label
        {
            Text = entry.Entry.IsGroup ? "◆" : "•",
            FontSize = 13,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center
        };
        marker.SetAppThemeColor(Label.TextColorProperty,
            GetThemeColor("Primary", "#FE2C55"), GetThemeColor("Primary", "#FE2C55"));
        Grid.SetColumn(marker, 1);
        grid.Children.Add(marker);

        var title = new Label { Text = entry.Title, FontSize = 14, FontFamily = "InterMedium" };
        var subtitle = new Label { Text = entry.Subtitle, FontSize = 12 };
        var infoStack = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        infoStack.Children.Add(title);
        infoStack.Children.Add(subtitle);
        Grid.SetColumn(infoStack, 2);
        grid.Children.Add(infoStack);

        var editButton = new Button
        {
            Text = Localization.T("Fr_Edit"),
            BackgroundColor = Colors.Transparent,
            FontSize = 12,
            Padding = new Thickness(8),
            HeightRequest = 44,
            VerticalOptions = LayoutOptions.Center
        };
        editButton.SetAppThemeColor(Button.TextColorProperty,
            GetThemeColor("Gray400", "#8B8F96"), GetThemeColor("Gray400", "#8B8F96"));
        editButton.Clicked += async (s, e) => await EditEntryAsync(entry);
        Grid.SetColumn(editButton, 3);
        grid.Children.Add(editButton);

        var row = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(10, 6)
        };
        row.SetAppThemeColor(Border.BackgroundColorProperty,
            GetThemeColor("Gray100", "#F3F4F6"), GetThemeColor("Gray900", "#111827"));
        row.Content = grid;

        // Redraws this row from its entry. A correction is written onto the entry itself, so this is what
        // carries it to the screen - and an entry that is not ready says so on its own row rather than in
        // a message after Add was pressed.
        void Refresh()
        {
            check.IsChecked = entry.Include;
            marker.Text = entry.Entry.IsGroup ? "◆" : "•";
            title.Text = entry.Title;
            subtitle.Text = entry.Subtitle;
            subtitle.TextColor = entry.IsReady
                ? GetThemeColor("Gray400", "#8B8F96")
                : GetThemeColor("DeleteColor", "#EE1D52");

            // A row that will not be added is dimmed, so the selection reads at a glance down a long list.
            row.Opacity = entry.Include ? 1.0 : 0.45;
        }

        check.CheckedChanged += (s, e) =>
        {
            entry.Include = e.Value;
            Refresh();
            RefreshDraftSummary();
        };

        _rowRefreshers.Add(Refresh);
        Refresh();

        return row;
    }

    private void OnSelectAllClicked(object? sender, EventArgs e)
    {
        _draft.IncludeAll();
        RefreshRows();
    }

    private void OnSelectNoneClicked(object? sender, EventArgs e)
    {
        _draft.IncludeNone();
        RefreshRows();
    }

    /// <summary>Redraws every row from the draft, for a change that affects all of them at once.</summary>
    private void RefreshRows()
    {
        foreach (var refresh in _rowRefreshers)
            refresh();

        RefreshDraftSummary();
    }

    /// <summary>
    /// Corrects one entry: its name, its username, or whether it is a person or a group.
    /// <para>
    /// Offered as a list rather than three buttons on the row, because a row in a long list has room for
    /// one control and the three corrections are rare enough that a step is worth the space.
    /// </para>
    /// <para>
    /// Switching to a person asks for the username straight away when there is none: a person is matched
    /// by it, and leaving the row merely unusable would make the user find the problem themselves.
    /// </para>
    /// </summary>
    private async Task EditEntryAsync(ImportDraftEntry entry)
    {
        var config = entry.Entry;
        var typeChange = config.IsGroup ? Localization.T("Imp_ChangeToPerson") : Localization.T("Imp_ChangeToGroup");
        var changeName = Localization.T("Imp_ChangeName");
        var changeUsername = Localization.T("Imp_ChangeUsername");

        var choice = await DisplayActionSheet(entry.Title, Localization.T("Common_Cancel"), null,
            changeName, changeUsername, typeChange);

        if (choice == changeName)
        {
            var typed = await DisplayPromptAsync(Localization.T("Imp_ChangeName"),
                config.IsGroup
                    ? Localization.T("Imp_GroupNamePrompt")
                    : Localization.T("Imp_PersonNamePrompt"),
                initialValue: config.DisplayName ?? string.Empty);

            if (typed is null)
                return;

            if (!ContactEditRules.TrySetDisplayName(config, typed, out var nameError))
            {
                await DisplayAlert(Localization.T("Imp_NotSavedTitle"), nameError ?? Localization.T("Imp_NameUnusable"), Localization.T("Common_OK"));
                return;
            }
        }
        else if (choice == changeUsername)
        {
            var typed = await DisplayPromptAsync(Localization.T("Imp_ChangeUsername"),
                Localization.T("Imp_UsernamePrompt"),
                initialValue: config.Username ?? string.Empty);

            if (typed is null)
                return;

            if (!ContactEditRules.TrySetUsername(config, typed, out var usernameError))
            {
                await DisplayAlert(Localization.T("Imp_NotSavedTitle"), usernameError ?? Localization.T("Imp_UsernameUnusable"), Localization.T("Common_OK"));
                return;
            }
        }
        else if (choice == typeChange)
        {
            ContactEditRules.SetGroup(config, !config.IsGroup);

            if (!config.IsGroup && string.IsNullOrWhiteSpace(config.Username))
            {
                var typed = await DisplayPromptAsync(Localization.T("Imp_UsernameTitle"),
                    Localization.T("Imp_UsernameForThisOne"),
                    initialValue: string.Empty);

                if (typed is not null && !ContactEditRules.TrySetUsername(config, typed, out var usernameError))
                    await DisplayAlert(Localization.T("Imp_NotSavedTitle"), usernameError ?? Localization.T("Imp_UsernameUnusable"), Localization.T("Common_OK"));
            }
        }
        else
        {
            // Cancelled, or the sheet was dismissed.
            return;
        }

        RefreshRows();
    }

    private async void OnAddContactsClicked(object? sender, EventArgs e)
    {
        var toAdd = _draft.ToAdd();
        if (_isAdding || toAdd.Count == 0)
            return;

        _isAdding = true;
        RefreshDraftSummary();

        try
        {
            var all = _settingsService.GetFriendsList();
            all.AddRange(toAdd);

            if (!_settingsService.TrySaveFriendsList(all, out var error))
            {
                await DisplayAlert(Localization.T("Imp_CouldNotSaveTitle"), error ?? Localization.T("Imp_ListSaveFailed"), Localization.T("Common_OK"));
                return;
            }

            var skipped = _draft.IncludedCount - toAdd.Count;
            await DisplayAlert(Localization.T("Imp_AddedTitle"),
                (toAdd.Count == 1
                    ? string.Format(Localization.T("Imp_AddedOne"), toAdd.Count)
                    : string.Format(Localization.T("Imp_AddedMany"), toAdd.Count)) + " "
                + Localization.T("Imp_AddedAtEnd") + " " + Localization.T("Imp_AddedMoveHint")
                + (skipped > 0 ? " " + (skipped == 1
                    ? string.Format(Localization.T("Imp_SkippedOne"), skipped)
                    : string.Format(Localization.T("Imp_SkippedMany"), skipped)) : string.Empty),
                Localization.T("Common_OK"));

            await Navigation.PopAsync();
        }
        finally
        {
            _isAdding = false;
            RefreshDraftSummary();
        }
    }

    /// <summary>
    /// Throws the scanned list away.
    /// <para>
    /// The stored scan is cleared as well as the page being left, because the store is what this page reads
    /// on appearing: without clearing it, opening the page again would offer the same results back, which
    /// is not what discarding them means.
    /// </para>
    /// </summary>
    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        ContactScanHub.Store.Clear();
        StopPolling();
        await Navigation.PopAsync();
    }

    private Color GetThemeColor(string key, string fallbackHex = "#92979E")
    {
        if (Application.Current != null && Application.Current.Resources.TryGetValue(key, out var resource) && resource is Color color)
            return color;
        return Color.FromArgb(fallbackHex);
    }
}
