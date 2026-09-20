namespace TiktokStreakSaver.Services;

/// <summary>
/// The rules for what a TikTok WebView is allowed to do with a URL, shared by every page that hosts
/// one.
/// <para>
/// This logic used to live privately inside the login page. The contact importer needs exactly the
/// same rules, and a second copy of them would drift - the failure mode being a WebView that works
/// on one page and strands the user on a blank error page on another.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class WebViewNavigationPolicy
{
    /// <summary>
    /// Every scheme a WebView can genuinely load. Anything else is a hand-off to another installed
    /// app rather than page content.
    /// </summary>
    public static readonly string[] LoadableSchemes =
    {
        "http://", "https://", "about:", "data:", "blob:", "file:", "content:", "javascript:"
    };

    /// <summary>
    /// Whether the URL is page content the WebView can load on its own.
    /// <para>
    /// TikTok signals its native app to open with custom schemes (<c>snssdk1180://</c>,
    /// <c>snssdk1233://</c>, <c>intent://</c>). A WebView cannot load those: Android shows a blank
    /// "net::ERR_UNKNOWN_URL_SCHEME" error page in their place, which strands the user with no way
    /// forward. Those have to be cancelled instead.
    /// </para>
    /// </summary>
    public static bool IsLoadable(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        foreach (var scheme in LoadableSchemes)
        {
            if (url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Pulls the http(s) destination out of an app hand-off URL, e.g.
    /// <c>snssdk1233://...?params_url=https%3A%2F%2Fwww.tiktok.com%2F&amp;...</c>. Returns null when
    /// there is no usable http(s) destination to follow, so a cancelled hand-off stays cancelled
    /// rather than navigating somewhere unexpected.
    /// </summary>
    public static string? ExtractHttpDestination(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

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

    /// <summary>
    /// Whether TikTok has answered with a sign-in page.
    /// <para>
    /// Worth detecting explicitly: a session can be rejected even when the app still believes it is
    /// signed in, and without this the page would wait for a chat list that is never coming.
    /// </para>
    /// </summary>
    public static bool IsLoginWall(string? url) =>
        !string.IsNullOrEmpty(url) && url.Contains("/login", StringComparison.OrdinalIgnoreCase);
}
