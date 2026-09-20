namespace TiktokStreakSaver.Services;

/// <summary>
/// TikTok WebView configuration and login detection.
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class TikTokWebViewHelper
{
    public const string LoginUrl = "https://www.tiktok.com/login";
    public const string MessagesUrl = "https://www.tiktok.com/messages";

    [Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
    public class LoginStatusResult
    {
        public bool IsLoggedIn { get; set; }
        public string Url { get; set; } = string.Empty;
        public bool IsValidUrl { get; set; }
    }

    /// <summary>
    /// Stop loading and release the login WebView so TikTok video/audio does not continue in the background.
    /// <para>
    /// This is best-effort cleanup that runs while the page owning the WebView is being removed, so it
    /// never throws. It deliberately does NOT call <c>webView.Handler.DisconnectHandler()</c>: MAUI owns
    /// the handler lifetime and disconnecting a handler whose platform view is still attached to the live
    /// visual tree leaves the native <c>Android.Webkit.WebView</c> orphaned (it is then never destroyed or
    /// cleaned up when the page is popped). That manual disconnect is what crashed the app right after a
    /// successful login.
    /// </para>
    /// </summary>
    public static void TearDownLoginWebView(WebView webView)
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                // Persist the session cookies produced by the login before the WebView goes away;
                // otherwise the in-memory CookieManager contents can be lost with the WebView.
                FlushCookies();

                if (webView.Handler?.PlatformView is Android.Webkit.WebView awv)
                {
                    awv.StopLoading();
                    awv.OnPause();

                    // NOTE: deliberately not calling awv.PauseTimers() here. PauseTimers() is a global
                    // request that pauses layout, parsing and JavaScript timers for EVERY WebView in the
                    // process - including the headless automation WebView owned by StreakService - and
                    // ResumeTimers() is never called anywhere, which would leave that WebView frozen.
                    awv.LoadUrl("about:blank");
                }

                webView.Source = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Login WebView teardown failed: {ex}");
            }
        });
#elif IOS
        Platforms.iOS.Services.IosWebViewConfigurator.TearDownLoginWebView(webView);
#else
        webView.Source = null;
#endif
    }

    public static void ConfigureWebView(WebView webView, string? customUserAgent = null)
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var androidWebView = webView.Handler?.PlatformView as Android.Webkit.WebView;
            if (androidWebView != null)
                ConfigureAndroidWebView(androidWebView, customUserAgent);
        });
#elif IOS
        Platforms.iOS.Services.IosWebViewConfigurator.Configure(webView, customUserAgent);
#endif
    }

#if ANDROID
    public static void ConfigureAndroidWebView(Android.Webkit.WebView webView, string? customUserAgent = null)
    {
        webView.Settings.JavaScriptEnabled = true;
        webView.Settings.DomStorageEnabled = true;
        webView.Settings.DatabaseEnabled = true;
        webView.Settings.CacheMode = Android.Webkit.CacheModes.Normal;
        webView.Settings.UserAgentString = string.IsNullOrEmpty(customUserAgent)
            ? GetDefaultUserAgent()
            : customUserAgent;

        var cookieManager = Android.Webkit.CookieManager.Instance;
        cookieManager?.SetAcceptCookie(true);
        cookieManager?.SetAcceptThirdPartyCookies(webView, true);
    }

    /// <summary>
    /// Configures a WebView for reading TikTok's inbox.
    /// <para>
    /// The wide viewport is the whole reason this exists separately from
    /// <see cref="ConfigureAndroidWebView"/>. A desktop user agent is not enough on its own: the
    /// layout TikTok serves follows the viewport width, so a WebView only as wide as the phone gets
    /// the mobile layout even with a desktop agent, and the conversation rows in that layout do not
    /// match the selectors the sending flow relies on. The result is a page where the list is
    /// plainly on screen and the scanner still finds nothing. StreakService has always forced this;
    /// the importer did not.
    /// </para>
    /// </summary>
    public static void ConfigureAndroidScannerWebView(Android.Webkit.WebView webView, string? customUserAgent)
    {
        ConfigureAndroidWebView(webView, customUserAgent);

        webView.Settings.UseWideViewPort = true;
        webView.Settings.LoadWithOverviewMode = true;
    }

    public static void FlushCookies() => Android.Webkit.CookieManager.Instance?.Flush();

    public static bool HasValidSessionCookie()
    {
        try
        {
            var cookieManager = Android.Webkit.CookieManager.Instance;
            if (cookieManager == null) return false;

            string cookies1 = cookieManager.GetCookie("https://www.tiktok.com") ?? string.Empty;
            string cookies2 = cookieManager.GetCookie("https://tiktok.com") ?? string.Empty;

            return cookies1.Contains("sessionid=") || cookies2.Contains("sessionid=");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error checking session cookie: {ex.Message}");
            return false;
        }
    }

    public static void ClearAllCookies()
    {
        try
        {
            var cookieManager = Android.Webkit.CookieManager.Instance;
            if (cookieManager != null)
            {
                cookieManager.RemoveAllCookies(null);
                cookieManager.Flush();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error clearing cookies: {ex.Message}");
        }
    }
#elif IOS
    public static bool HasValidSessionCookie() =>
        Platforms.iOS.Services.IosWebViewConfigurator.HasValidSessionCookie();

    public static Task<bool> HasValidSessionCookieAsync(WebView? webView = null) =>
        Platforms.iOS.Services.IosWebViewConfigurator.HasValidSessionCookieInWebViewAsync(webView);

    public static void ClearAllCookies()
    {
        Platforms.iOS.Services.IosWebViewConfigurator.ClearCookies();
    }
#else
    public static bool HasValidSessionCookie() => false;
    public static void ClearAllCookies() { }
#endif

    public static string GetDefaultUserAgent() =>
        "Mozilla/5.0 (Linux; Android 10; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";

    public static LoginStatusResult CheckLoginStatus(string? url)
    {
        var result = new LoginStatusResult { Url = url ?? string.Empty };

        if (string.IsNullOrEmpty(url))
        {
            result.IsValidUrl = false;
            result.IsLoggedIn = false;
            return result;
        }

        var urlLower = url.ToLower();
        if (!urlLower.StartsWith("http"))
        {
            result.IsValidUrl = false;
            result.IsLoggedIn = false;
            return result;
        }

        result.IsValidUrl = true;
        if (urlLower.Contains("/login"))
        {
            result.IsLoggedIn = false;
            return result;
        }

        if (urlLower.Contains("tiktok.com/messages") ||
            urlLower.Contains("tiktok.com/foryou") ||
            urlLower.Contains("tiktok.com/@"))
        {
            result.IsLoggedIn = true;
            return result;
        }

        result.IsLoggedIn = false;
        return result;
    }

    public static void UpdateSessionStatus(SessionService sessionService, bool isLoggedIn)
    {
#if IOS
        if (isLoggedIn)
        {
            if (!sessionService.TrySetSessionValid(true, out _))
                sessionService.SetSessionValid(true);
            Storage.AppStorageProvider.Current.SetBool(AppConstants.AuthRequiredKey, false);
            return;
        }

        sessionService.TrySetSessionValid(false, out _);
#elif ANDROID
        sessionService.SetSessionValid(isLoggedIn);
        if (isLoggedIn)
            FlushCookies();
#else
        sessionService.SetSessionValid(isLoggedIn);
#endif
    }
}
