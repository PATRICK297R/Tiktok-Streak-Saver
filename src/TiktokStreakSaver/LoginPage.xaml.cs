using TiktokStreakSaver.Services;
using TiktokStreakSaver.Services.Storage;

namespace TiktokStreakSaver;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class LoginPage : ContentPage
{
    private readonly SessionService _sessionService;
    private readonly SettingsService _settingsService;
    private bool _isLoggedIn;
    private bool _webViewTornDown;
    private bool _completionInProgress;

    public LoginPage()
    {
        InitializeComponent();
        _sessionService = new SessionService();
        _settingsService = new SettingsService();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _webViewTornDown = false;
        _isLoggedIn = false;
        _completionInProgress = false;
        LoadTikTok();
        await TryCompleteExistingSessionAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        TearDownLoginWebView();
    }

    private void LoadTikTok()
    {
        LoadingOverlay.IsVisible = true;

        var desktopUa = AppConstants.DesktopChromeUserAgent;
#if ANDROID || IOS
        TikTokWebViewHelper.ConfigureWebView(TikTokWebView, desktopUa);
        _sessionService.SetLoginUserAgent(desktopUa);
#endif

        TikTokWebView.Source = TikTokWebViewHelper.LoginUrl;
    }

    private async Task TryCompleteExistingSessionAsync()
    {
        await Task.Delay(800);
        if (_isLoggedIn || _completionInProgress)
            return;

#if IOS
        Platforms.iOS.Services.IosWebViewConfigurator.AttachWebView(TikTokWebView);
        if (!await TikTokWebViewHelper.HasValidSessionCookieAsync(TikTokWebView))
            return;

        _isLoggedIn = true;
        await Platforms.iOS.Services.IosWebViewConfigurator.ExportCookiesFromCurrentWebViewAsync();
        await SessionRefreshHelper.RefreshAndGetRunReadyAsync(_sessionService);
        await Done(showSuccessAlert: false);
#elif ANDROID
        if (!TikTokWebViewHelper.HasValidSessionCookie())
            return;

        _isLoggedIn = true;
        await Done(showSuccessAlert: false);
#endif
    }

    private async void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (_isLoggedIn || _completionInProgress) return;
        LoadingOverlay.IsVisible = false;

        bool hasSession;
#if IOS
        Platforms.iOS.Services.IosWebViewConfigurator.AttachWebView(TikTokWebView);
        hasSession = await TikTokWebViewHelper.HasValidSessionCookieAsync(TikTokWebView);
        if (!hasSession && TikTokWebViewHelper.CheckLoginStatus(e.Url).IsLoggedIn)
        {
            await Task.Delay(400);
            hasSession = await TikTokWebViewHelper.HasValidSessionCookieAsync(TikTokWebView);
        }
#else
        hasSession = TikTokWebViewHelper.HasValidSessionCookie();
#endif

        if (!hasSession)
            return;

        _isLoggedIn = true;
#if IOS
        await Platforms.iOS.Services.IosWebViewConfigurator.ExportCookiesFromCurrentWebViewAsync();
#endif
        await Done();
    }

    private void OnWebViewNavigating(object? sender, WebNavigatingEventArgs e)
    {
        var url = e.Url;
        if (string.IsNullOrEmpty(url))
            return;

        // Every scheme the login page legitimately navigates to. Anything else is a hand-off to
        // another installed app rather than page content.
        if (IsWebViewLoadableScheme(url))
            return;

        // TikTok signals its native app to open with custom schemes (snssdk1180://, snssdk1233://,
        // intent://, ...). A WebView cannot load those: Android shows a blank
        // "net::ERR_UNKNOWN_URL_SCHEME" error page in their place, which strands the login flow and
        // leaves the user with no way forward. Cancel the hand-off; when the URL carries the real
        // destination in its params_url query parameter, follow that instead.
        e.Cancel = true;

        var target = ExtractParamsUrl(url);
        if (target != null)
            TikTokWebView.Source = target;
    }

    private static bool IsWebViewLoadableScheme(string url)
    {
        foreach (var scheme in LoadableSchemes)
        {
            if (url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static readonly string[] LoadableSchemes =
    {
        "http://", "https://", "about:", "data:", "blob:", "file:", "content:", "javascript:"
    };

    /// <summary>
    /// Pulls the http(s) destination out of an app hand-off URL, e.g.
    /// <c>snssdk1233://...?params_url=https%3A%2F%2Fwww.tiktok.com%2F&amp;...</c>. Returns null when the
    /// URL has no usable http(s) destination.
    /// </summary>
    private static string? ExtractParamsUrl(string url)
    {
        const string key = "params_url=";
        var index = url.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var value = url.Substring(index + key.Length);
        var end = value.IndexOfAny(new[] { '&', '#' });
        if (end >= 0)
            value = value.Substring(0, end);

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(value);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not decode params_url '{value}': {ex.Message}");
            return null;
        }

        return decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? decoded : null;
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        if (TikTokWebView.CanGoBack)
        {
            TikTokWebView.GoBack();
        }
        else if (Navigation.NavigationStack.Count > 1)
        {
            await Navigation.PopAsync();
        }
        else if (Navigation.ModalStack.Count > 0)
        {
            await Navigation.PopModalAsync();
        }
        else if (Shell.Current != null)
        {
            await Shell.Current.GoToAsync("//DashboardPage");
        }
    }

    private void OnRefreshClicked(object? sender, EventArgs e)
    {
        LoadingOverlay.IsVisible = true;
        TikTokWebView.Reload();
    }

    private void TearDownLoginWebView()
    {
        if (_webViewTornDown)
            return;
        _webViewTornDown = true;
        TikTokWebViewHelper.TearDownLoginWebView(TikTokWebView);
    }

    private async Task Done(bool showSuccessAlert = true)
    {
        if (_completionInProgress)
            return;
        _completionInProgress = true;

        // NOTE: the login WebView is intentionally NOT torn down here. Doing it before the session was
        // persisted - and before the "Logged In" alert and the navigation completed - disconnected the
        // WebView handler while its platform view was still attached to the live page, which closed the
        // app as soon as the alert was dismissed. OnDisappearing performs the teardown instead, once the
        // page really is going away.
        if (_isLoggedIn)
        {
            if (!_sessionService.TrySetSessionValid(true, out var persistError))
            {
                _completionInProgress = false;
                await DisplayAlert("Could Not Save Session",
                    persistError ?? "Login succeeded but session state did not persist on this device.", "OK");
                return;
            }

#if ANDROID
            // Flush the cookies this login produced to disk before the WebView is torn down in
            // OnDisappearing, so the session survives the teardown and the next cold start.
            TikTokWebViewHelper.FlushCookies();
#endif

            AppStorageProvider.Current.SetBool(AppConstants.AuthRequiredKey, false);

            bool justEnabled = false;
            if (!_settingsService.IsScheduled())
            {
#if ANDROID
                var context = Platform.CurrentActivity ?? Android.App.Application.Context;
                TiktokStreakSaver.Platforms.Android.StreakScheduler.ScheduleNextRun(context);
#endif
                justEnabled = true;
            }

            SessionState.NotifyChanged();

            if (showSuccessAlert)
            {
                var body = justEnabled
#if IOS
                    ? "You're logged in to TikTok! Set up a daily Shortcut (see Profile) to run streaks automatically."
#else
                    ? "You're logged in to TikTok! Background automation has been enabled — your streaks will run on the schedule set in Profile."
#endif
                    : "You're logged in to TikTok! The app will use this session for streak messaging.";
                await DisplayAlert("Logged In", body, "OK");
            }

            if (Navigation.NavigationStack.Count > 1)
            {
                await Navigation.PopAsync();
            }
            else if (Navigation.ModalStack.Count > 0)
            {
                await Navigation.PopModalAsync();
            }
            else if (Shell.Current != null)
            {
                await Shell.Current.GoToAsync("//DashboardPage");
            }
        }
        else
        {
            _sessionService.TrySetSessionValid(false, out _);
            await DisplayAlert("Not Logged In",
                "Please login to TikTok first before continuing.", "OK");
            _completionInProgress = false;
        }
    }
}
