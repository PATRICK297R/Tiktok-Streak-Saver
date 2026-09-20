using Microsoft.Maui.Controls.Shapes;
using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;

namespace TiktokStreakSaver.Pages;

/// <summary>
/// Reads the conversations in the user's TikTok inbox and offers to add the ones that are not
/// already in the list.
/// <para>
/// The scanning itself is done by <c>tiktok_read_contacts.js</c>, which reports into
/// <c>window.__saverScan</c>; this page polls that object. Polling one JSON value avoids marshalling
/// strings across the JavaScript bridge, and it keeps the page's only job - deciding what to add -
/// in C# where it shares its matching rules with the backup restore.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class ImportContactsPage : ContentPage
{
    private const string ScanScriptName = "tiktok_read_contacts.js";

    private readonly SettingsService _settingsService;
    private readonly SessionService _sessionService;

    private string _scanScript = string.Empty;
    private IDispatcherTimer? _watchdogTimer;
    private bool _scanFinished;
    private bool _scriptReady;
    private bool _pageLoaded;
    private bool _isInjecting;
    private bool _isAdding;
    private bool _reportReceived;

    /// <summary>
    /// The last status the scanner reported. Held so the watchdog can put it back after it has shown
    /// its own "waiting" text, instead of leaving a message that no longer describes anything.
    /// </summary>
    private string _lastStatus = string.Empty;

    private List<FriendConfig> _found = new();

#if ANDROID
    /// <summary>The platform WebView the JavaScript interface is currently attached to.</summary>
    private Android.Webkit.WebView? _interfaceHost;
#endif

    /// <summary>
    /// When the page started trying to read the inbox, and when the script was last sent. Both are
    /// needed: injections are repeated while nothing comes back, so a single "injected at" time
    /// cannot also serve as the overall deadline.
    /// </summary>
    private DateTime _startedAt;
    private DateTime _lastInjectionAt;
    private int _injectionAttempts;

    public ImportContactsPage()
    {
        InitializeComponent();
        _settingsService = new SettingsService();
        _sessionService = new SessionService();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // The inbox needs a session; without one TikTok shows a login wall and the scan would
        // report "nothing found", which reads like a bug rather than a missing login.
        if (!_sessionService.IsSessionValid())
        {
            await DisplayAlert("Login required",
                "Sign in to TikTok first, then import your chats.",
                "OK");
            await Navigation.PopAsync();
            return;
        }

        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync(ScanScriptName);
            using var reader = new System.IO.StreamReader(stream);

            // Flattened before injection, exactly as the sending flow prepares its script: the
            // Android WebView does not reliably evaluate a multi-line script, and the failure is
            // silent - the page then waits for a scanner that never ran.
            _scanScript = ScriptPreparation.ForInjection(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            ScanStatusLabel.Text = "The scanner could not be loaded.";
            await DisplayAlert("Import unavailable", $"Could not load the scanner: {ex.Message}", "OK");
            return;
        }

        _scriptReady = true;

        // The page drives itself from here: the watchdog loads the inbox if the platform view was
        // not ready yet, keeps sending the script until it reports, and gives up with an explanation.
        // Making it a driver rather than a deadline matters, because a single injection attempt that
        // lands at the wrong moment leaves the page waiting with nothing to show for it.
        StartWatchdog();

        // The platform WebView may not exist yet while the page is appearing, and configuring one
        // that does not exist does nothing at all - which is how the first load went out with the
        // framework's default user agent and came up as TikTok's mobile page. HandlerChanged is the
        // point where the platform view is guaranteed to be there.
        ScanWebView.HandlerChanged -= OnScanWebViewHandlerChanged;
        ScanWebView.HandlerChanged += OnScanWebViewHandlerChanged;
        LoadInboxIfPossible();
    }

    private void OnScanWebViewHandlerChanged(object? sender, EventArgs e) => LoadInboxIfPossible();

    /// <summary>
    /// Configures the WebView and then loads the inbox, in that order and only once.
    /// <para>
    /// The order is the whole point: the user agent has to be in place before the first request,
    /// because the session cookies were issued against it. Loading first and configuring afterwards
    /// presents TikTok with a different client, which answers with the mobile layout or a sign-in
    /// page instead of the inbox.
    /// </para>
    /// </summary>
    private void LoadInboxIfPossible()
    {
        if (_pageLoaded || !_scriptReady)
            return;

#if ANDROID
        // Configured directly on the platform view rather than through
        // TikTokWebViewHelper.ConfigureWebView, which defers the work to the main thread. That
        // deferral is harmless when nothing follows it, but here the load starts immediately
        // afterwards - so the user agent could be applied after the first request had already gone
        // out. That is how the inbox came up as TikTok's mobile page on a first visit.
        var platformView = ScanWebView.Handler?.PlatformView as Android.Webkit.WebView;
        if (platformView is null)
            return;

        TikTokWebViewHelper.ConfigureAndroidWebView(platformView, ResolveUserAgent());
        EnsureJsInterface(platformView);
#else
        if (ScanWebView.Handler is null)
            return;

        TikTokWebViewHelper.ConfigureWebView(ScanWebView, ResolveUserAgent());
#endif

        _pageLoaded = true;
        ScanWebView.Source = new UrlWebViewSource { Url = TikTokWebViewHelper.MessagesUrl };
    }

#if ANDROID
    /// <summary>
    /// Attaches the bridge the scanner reports through.
    /// <para>
    /// This is the mechanism the sending flow already uses to hear back from its script, and the one
    /// that is proven on this platform. It belongs to the platform WebView, so it survives
    /// navigations and only needs re-attaching when the platform view itself is replaced.
    /// </para>
    /// </summary>
    private void EnsureJsInterface(Android.Webkit.WebView platformView)
    {
        if (ReferenceEquals(_interfaceHost, platformView))
            return;

        platformView.AddJavascriptInterface(new ScanJsInterface(this), ContactScanReader.BridgeName);
        _interfaceHost = platformView;
    }
#endif

    /// <summary>
    /// The user agent the session was created with, falling back to the very same constant the login
    /// page uses. A different string would present a different client and the session would be
    /// rejected.
    /// </summary>
    private string ResolveUserAgent()
    {
        var loginUserAgent = _sessionService.GetLoginUserAgent();
        return string.IsNullOrWhiteSpace(loginUserAgent) ? AppConstants.DesktopChromeUserAgent : loginUserAgent;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopWatchdog();
        ScanWebView.HandlerChanged -= OnScanWebViewHandlerChanged;

        // The same teardown the login WebView uses: it stops loading and releases the page without
        // disconnecting a handler MAUI still owns.
        TikTokWebViewHelper.TearDownLoginWebView(ScanWebView);
    }

    private void OnScanNavigating(object? sender, WebNavigatingEventArgs e)
    {
        var url = e.Url;
        if (string.IsNullOrEmpty(url))
            return;

        if (WebViewNavigationPolicy.IsLoadable(url))
            return;

        // TikTok tries to open its own app with custom schemes, which a WebView cannot load - Android
        // shows a blank error page instead and the scan is stranded. Cancel and follow the real
        // destination when the URL carries one.
        e.Cancel = true;

        var target = WebViewNavigationPolicy.ExtractHttpDestination(url);
        if (target != null)
            ScanWebView.Source = target;
    }

    private async void OnScanNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (_scanFinished)
            return;

        // A session can be rejected even when the app still believes it is signed in. Without this
        // the page would sit waiting for a chat list that is never coming.
        if (WebViewNavigationPolicy.IsLoginWall(e.Url))
        {
            StopWatchdog();
            ScanStatusLabel.Text = "TikTok asked you to sign in again.";
            await DisplayAlert("Sign in again",
                "TikTok no longer accepts the saved session. Sign in again from the Profile page, then come back to import.",
                "OK");
            await Navigation.PopAsync();
            return;
        }

        // Injected on every navigation until results arrive. The script guards against restarting
        // itself, and re-injecting matters because a full page navigation somewhere in TikTok's flow
        // wipes the page's JavaScript context - taking a scan that was already running with it.
        InjectScanner();
    }

    /// <summary>
    /// Sends the scanner into the page.
    /// <para>
    /// This uses the platform WebView's own call, which is what the sending flow uses. MAUI's
    /// <c>EvaluateJavaScriptAsync</c> wrapper is deliberately not used: the importer was the only
    /// place in the app relying on it, and it produced a page where the script had plainly never run
    /// while the call itself reported no error.
    /// </para>
    /// </summary>
    private void InjectScanner()
    {
        if (_scanFinished || _isInjecting || string.IsNullOrEmpty(_scanScript))
            return;

        _isInjecting = true;
        try
        {
#if ANDROID
            var platformView = ScanWebView.Handler?.PlatformView as Android.Webkit.WebView;
            if (platformView is null)
                return;

            EnsureJsInterface(platformView);
            platformView.EvaluateJavascript(_scanScript, null);
#else
            _ = ScanWebView.EvaluateJavaScriptAsync(_scanScript);
#endif

            // Recorded so the watchdog knows when to retry, and so the page can report how many
            // attempts were made when the script never comes back.
            _lastInjectionAt = DateTime.Now;
            _injectionAttempts++;
        }
        catch (Exception ex)
        {
            // Worth surfacing rather than only logging: a script that cannot be injected looks
            // exactly like an inbox with no conversations in it.
            System.Diagnostics.Debug.WriteLine($"Contact scan injection failed: {ex}");
            ScanStatusLabel.Text = "The scanner could not start on this page.";
        }
        finally
        {
            _isInjecting = false;
        }
    }

    // ─── Receiving reports ───────────────────────────────────────────────────

#if ANDROID
    /// <summary>
    /// The bridge the scanner reports through.
    /// <para>
    /// Reports arrive on the WebView's JavaScript thread, so everything that touches the page is
    /// marshalled back to the UI thread - the same arrangement the sending flow uses.
    /// </para>
    /// </summary>
    [Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
    internal sealed class ScanJsInterface : Java.Lang.Object
    {
        private readonly ImportContactsPage _page;

        public ScanJsInterface(ImportContactsPage page)
        {
            _page = page;
        }

        [Android.Webkit.JavascriptInterface]
        [Java.Interop.Export("report")]
        public void Report(string json)
        {
            _page.MainThreadDispatcher(() => _page.OnScanReported(json));
        }
    }
#endif

    /// <summary>Marshals a report onto the UI thread, wherever it came from.</summary>
    private void MainThreadDispatcher(Action action) => MainThread.BeginInvokeOnMainThread(action);

    /// <summary>
    /// Handles one report from the page.
    /// <para>
    /// Because the scanner pushes on every line it logs, the very first report already proves the
    /// script is running. That is what makes it possible to say which of "the script never ran" and
    /// "the script ran and read nothing" actually happened.
    /// </para>
    /// </summary>
    internal void OnScanReported(string json)
    {
        if (_scanFinished || _isAdding)
            return;

        var normalized = ContactScanReader.NormalizeJavaScriptResult(json);
        if (!ContactScanReader.TryParse(normalized, out var scan, out _) || scan is null)
            return;

        _reportReceived = true;
        UpdateScanStatus(scan);

        if (scan.Done)
            ShowResults(scan);
    }

    // ─── Watchdog ────────────────────────────────────────────────────────────

    private void StartWatchdog()
    {
        if (_watchdogTimer is null)
        {
            _watchdogTimer = Dispatcher.CreateTimer();
            _watchdogTimer.Interval = TimeSpan.FromSeconds(2);
            _watchdogTimer.Tick += OnWatchdogTick;
        }

        _watchdogTimer.Start();
        _startedAt = DateTime.Now;
        _lastInjectionAt = DateTime.MinValue;
    }

    private void StopWatchdog()
    {
        if (_watchdogTimer is null)
            return;

        _watchdogTimer.Stop();
        _watchdogTimer.Tick -= OnWatchdogTick;
        _watchdogTimer = null;
    }

    /// <summary>
    /// Drives the scan: loads the inbox if the platform view was not ready, keeps sending the script
    /// until the page reports, and gives up with an explanation once it is clear nothing will.
    /// <para>
    /// The retrying is the important part. Sending the script once and hoping relies on that one
    /// attempt landing after the page is ready; a retry costs nothing because the script refuses to
    /// restart itself, and it recovers from an injection that was too early, a page that navigated
    /// afterwards, and a platform view that only appeared a moment later.
    /// </para>
    /// </summary>
    private async void OnWatchdogTick(object? sender, EventArgs e)
    {
        if (_scanFinished || _isAdding)
            return;

        // Still no platform view when the page appeared: try again every tick until there is one.
        LoadInboxIfPossible();

        // A report means the scanner is running. It enforces its own deadline and will publish a
        // result, so there is nothing to chase.
        if (_reportReceived)
        {
            ScanStatusLabel.Text = _lastStatus;
            return;
        }

        if (DateTime.Now - _lastInjectionAt > ReInjectEvery)
        {
            InjectScanner();
            ScanStatusLabel.Text = $"Waiting for the scanner to start… (attempt {_injectionAttempts})";
            return;
        }

        if (DateTime.Now - _startedAt > ScanDeadline)
            await GiveUpOnScanAsync("The scanner did not start in the page");
    }

    /// <summary>
    /// How long to wait for the first report before concluding the script cannot run on this page.
    /// Generous, because it is retrying the whole time rather than waiting on one attempt.
    /// </summary>
    private static readonly TimeSpan ScanDeadline = TimeSpan.FromSeconds(90);

    /// <summary>How often to resend the script while nothing has come back.</summary>
    private static readonly TimeSpan ReInjectEvery = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Shows what the scanner is doing.
    /// <para>
    /// The scanner opens each conversation in turn to read its real name, which takes a moment per
    /// chat, so the count of chats checked is shown too. Without it a scan that is working steadily
    /// looks like one that has stalled.
    /// </para>
    /// </summary>
    private void UpdateScanStatus(ContactScanDto scan)
    {
        var text = scan.Items.Count > 0
            ? $"Reading your chats… {scan.Items.Count} found, {scan.Attempts} checked"
            : scan.Attempts > 0
                ? $"Reading your chats… opening chat {scan.Attempts}"
                : "Reading your chats…";

        _lastStatus = text;

        if (text != ScanStatusLabel.Text)
            ScanStatusLabel.Text = text;
    }

    private async Task GiveUpOnScanAsync(string title)
    {
        if (_scanFinished)
            return;

        _scanFinished = true;
        StopWatchdog();

        ScanStatusLabel.Text = "Nothing could be read from the inbox.";

        // The wording depends on which of the two failures this was, because they are fixed in
        // completely different places. The counts are included because they are what distinguishes
        // "the page never loaded" from "the script was sent but the page refused it".
        var body = _reportReceived
            ? "The scanner ran in the page but could not read the conversation list, so TikTok's page layout is probably not what it expects."
            : $"The scanner did not run in the page at all, so nothing was ever read - this is a fault in the app, not in your inbox. "
              + $"(page loaded: {(_pageLoaded ? "yes" : "no")}, script sent {_injectionAttempts} time(s)).";

        await DisplayAlert(title,
            body + "\n\nOpen the inbox once from the Dashboard, make sure you are signed in, then try again.",
            "OK");
        await Navigation.PopAsync();
    }

    // ─── Results ─────────────────────────────────────────────────────────────

    private async void ShowResults(ContactScanDto scan)
    {
        _scanFinished = true;
        StopWatchdog();

        var scanned = ContactScanReader.ToFriends(scan.Items);
        _found = ContactScanReader.SelectNew(_settingsService.GetFriendsList(), scanned);
        var alreadyKnown = scanned.Count - _found.Count;

        ScanWebView.IsVisible = false;
        CancelScanButton.IsVisible = false;

        if (_found.Count == 0)
        {
            ScanStatusLabel.Text = scanned.Count == 0
                ? "No conversations were read from your inbox."
                : "Everyone found is already on your list.";

            await DisplayAlert("Nothing to add",
                scanned.Count == 0
                    ? ContactScanReader.DescribeEmptyScan(scan)
                    : $"Read {scanned.Count} conversations, and all of them are already on your list.",
                "OK");

            await Navigation.PopAsync();
            return;
        }

        var friends = _found.Count(f => !f.IsGroup);
        var groups = _found.Count - friends;

        ResultsSummaryLabel.Text = $"{_found.Count} new to add";
        ResultsDetailLabel.Text = $"Read {scanned.Count} conversations: {friends} people and {groups} groups. "
                                + (alreadyKnown > 0 ? $"{alreadyKnown} were already on your list. " : string.Empty)
                                + "They will be added to the end of your send order.";

        var rowsToRemove = ResultsList.Children.ToList();
        foreach (var row in rowsToRemove) ResultsList.Children.Remove(row);
        foreach (var entry in _found)
            ResultsList.Children.Add(CreateResultRow(entry));

        AddContactsButton.Text = $"Add {_found.Count}";

        ResultsPanel.IsVisible = true;
        await Task.CompletedTask;
    }

    private View CreateResultRow(FriendConfig entry)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = new GridLength(26) },
                new ColumnDefinition { Width = GridLength.Star }
            },
            ColumnSpacing = 6
        };

        var marker = new Label
        {
            Text = entry.IsGroup ? "◆" : "•",
            FontSize = 13,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center
        };
        marker.SetAppThemeColor(Label.TextColorProperty,
            GetThemeColor("Primary", "#FE2C55"), GetThemeColor("Primary", "#FE2C55"));
        grid.Children.Add(marker);

        var displayName = string.IsNullOrEmpty(entry.DisplayName) ? entry.Username : entry.DisplayName;
        var infoStack = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        infoStack.Children.Add(new Label { Text = displayName, FontSize = 14, FontFamily = "InterMedium" });
        infoStack.Children.Add(new Label
        {
            Text = entry.IsGroup ? "Group" : $"@{entry.Username}",
            FontSize = 12,
            TextColor = GetThemeColor("Gray400", "#8B8F96")
        });
        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        var row = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(10, 6)
        };
        row.SetAppThemeColor(Border.BackgroundColorProperty,
            GetThemeColor("Gray100", "#F3F4F6"), GetThemeColor("Gray900", "#111827"));
        row.Content = grid;
        return row;
    }

    private async void OnAddContactsClicked(object? sender, EventArgs e)
    {
        if (_isAdding || _found.Count == 0)
            return;

        _isAdding = true;
        AddContactsButton.IsEnabled = false;

        try
        {
            var all = _settingsService.GetFriendsList();
            all.AddRange(_found);

            if (!_settingsService.TrySaveFriendsList(all, out var error))
            {
                await DisplayAlert("Could not save", error ?? "The list could not be saved on this device.", "OK");
                return;
            }

            await DisplayAlert("Added",
                $"{_found.Count} entries added. They are at the end of your send order, so move them with Reorder on the Friends page if you want them earlier.",
                "OK");

            await Navigation.PopAsync();
        }
        finally
        {
            _isAdding = false;
            AddContactsButton.IsEnabled = true;
        }
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        StopWatchdog();
        await Navigation.PopAsync();
    }

    private Color GetThemeColor(string key, string fallbackHex = "#92979E")
    {
        if (Application.Current != null && Application.Current.Resources.TryGetValue(key, out var resource) && resource is Color color)
            return color;
        return Color.FromArgb(fallbackHex);
    }
}
