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
    private IDispatcherTimer? _pollTimer;
    private bool _scanFinished;
    private bool _scriptInjected;
    private bool _isAdding;
    private int _lastReportedCount = -1;
    private List<FriendConfig> _found = new();

    /// <summary>When polling began, so the page can give up instead of waiting forever.</summary>
    private DateTime _pollStartedAt;

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
            _scanScript = await reader.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            ScanStatusLabel.Text = "The scanner could not be loaded.";
            await DisplayAlert("Import unavailable", $"Could not load the scanner: {ex.Message}", "OK");
            return;
        }

        StartPolling();

        // Configure BEFORE the first request, mirroring LoginPage. The user agent in particular has
        // to match the one the session cookies were issued against: setting it after the first load
        // lets that load go out as a different client, and TikTok answers with a login wall instead
        // of the inbox.
        TikTokWebViewHelper.ConfigureWebView(ScanWebView, ResolveUserAgent());

        ScanWebView.Source = new UrlWebViewSource { Url = TikTokWebViewHelper.MessagesUrl };
    }

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
        StopPolling();

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
            StopPolling();
            ScanStatusLabel.Text = "TikTok asked you to sign in again.";
            await DisplayAlert("Sign in again",
                "TikTok no longer accepts the saved session. Sign in again from the Profile page, then come back to import.",
                "OK");
            await Navigation.PopAsync();
            return;
        }

        // Injecting on every navigation would restart a finished scan, so it happens once.
        if (_scriptInjected || string.IsNullOrEmpty(_scanScript))
            return;

        _scriptInjected = true;
        ScanStatusLabel.Text = "Reading your chats…";
        _ = InvokeScannerAsync();
    }

    private async Task InvokeScannerAsync()
    {
        try
        {
            await ScanWebView.EvaluateJavaScriptAsync(_scanScript);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Contact scan injection failed: {ex}");
            ScanStatusLabel.Text = "The scanner could not start on this page.";
        }
    }

    // ─── Polling ─────────────────────────────────────────────────────────────

    private void StartPolling()
    {
        if (_pollTimer is null)
        {
            _pollTimer = Dispatcher.CreateTimer();
            _pollTimer.Interval = TimeSpan.FromSeconds(1.5);
            _pollTimer.Tick += OnPollTick;
        }

        _pollTimer.Start();
        _pollStartedAt = DateTime.Now;
    }

    private void StopPolling()
    {
        if (_pollTimer is null)
            return;

        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTick;
        _pollTimer = null;
    }

    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_scanFinished || _isAdding || ScanWebView.Handler is null)
            return;

        string? raw;
        try
        {
            raw = await ScanWebView.EvaluateJavaScriptAsync(
                "window.__saverScan ? JSON.stringify(window.__saverScan) : null");
        }
        catch (Exception ex)
        {
            // A poll that lands while the page is navigating is expected, not a failure worth
            // reporting to the user.
            System.Diagnostics.Debug.WriteLine($"Contact scan poll failed: {ex.Message}");
            return;
        }

        var json = ContactScanReader.NormalizeJavaScriptResult(raw);
        if (!ContactScanReader.TryParse(json, out var scan, out _) || scan is null)
        {
            // The scanner has produced nothing at all. If that persists, the page has to say so
            // rather than leave "Reading your chats…" on screen forever.
            if (DateTime.Now - _pollStartedAt > ScanTimeout)
                await GiveUpOnScanAsync("The inbox could not be read");

            return;
        }

        if (scan.Items.Count != _lastReportedCount)
        {
            _lastReportedCount = scan.Items.Count;
            ScanStatusLabel.Text = $"Reading your chats… {scan.Items.Count} found so far";
        }

        if (scan.Done)
            ShowResults(scan);
    }

    /// <summary>
    /// How long to wait for the scanner before reporting that it did not work. Generous, because a
    /// long inbox means many scroll passes.
    /// </summary>
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(90);

    private async Task GiveUpOnScanAsync(string title)
    {
        if (_scanFinished)
            return;

        _scanFinished = true;
        StopPolling();

        ScanStatusLabel.Text = "Nothing could be read from the inbox.";
        await DisplayAlert(title,
            "TikTok's inbox did not respond to reading. Open the inbox once from the Dashboard, make sure you are signed in, then try again.",
            "OK");
        await Navigation.PopAsync();
    }

    // ─── Results ─────────────────────────────────────────────────────────────

    private async void ShowResults(ContactScanDto scan)
    {
        _scanFinished = true;
        StopPolling();

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
                    ? "No conversations were found. Open TikTok's inbox once in the app, then try again."
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
