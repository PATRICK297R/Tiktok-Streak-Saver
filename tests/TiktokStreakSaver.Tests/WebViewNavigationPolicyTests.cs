using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// These rules decide whether a TikTok WebView loads a URL or cancels it. They are shared by the
/// login page and the contact importer, so a regression here breaks both.
/// </summary>
public class WebViewNavigationPolicyTests
{
    [Theory]
    [InlineData("https://www.tiktok.com/messages")]
    [InlineData("https://www.tiktok.com/login")]
    [InlineData("http://example.com")]
    [InlineData("about:blank")]
    [InlineData("data:text/html,hi")]
    [InlineData("blob:https://www.tiktok.com/abc")]
    [InlineData("file:///android_asset/x.html")]
    [InlineData("content://media/1")]
    [InlineData("javascript:void(0)")]
    public void PageContentIsLoadable(string url)
    {
        Assert.True(WebViewNavigationPolicy.IsLoadable(url));
    }

    [Theory]
    [InlineData("snssdk1180://aweme/detail/123")]
    [InlineData("snssdk1233://challenge/detail/1")]
    [InlineData("intent://scan/#Intent;scheme=zxing;end")]
    [InlineData("market://details?id=com.zhiliaoapp.musically")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+123456")]
    public void AnotherAppIsNotLoadedInTheWebView(string url)
    {
        // Loading these gives a blank "net::ERR_UNKNOWN_URL_SCHEME" page instead, which strands the
        // user with no way forward.
        Assert.False(WebViewNavigationPolicy.IsLoadable(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMissingUrlIsNotLoadable(string? url)
    {
        Assert.False(WebViewNavigationPolicy.IsLoadable(url));
    }

    [Fact]
    public void TheSchemeCheckIsCaseInsensitive()
    {
        Assert.True(WebViewNavigationPolicy.IsLoadable("HTTPS://WWW.TIKTOK.COM/MESSAGES"));
        Assert.True(WebViewNavigationPolicy.IsLoadable("JavaScript:void(0)"));
    }

    [Fact]
    public void TheRealDestinationIsPulledOutOfAHandOff()
    {
        const string handoff =
            "snssdk1233://webview?params_url=https%3A%2F%2Fwww.tiktok.com%2Fmessages&other=1";

        Assert.Equal("https://www.tiktok.com/messages", WebViewNavigationPolicy.ExtractHttpDestination(handoff));
    }

    [Fact]
    public void TheDestinationStopsAtTheNextParameter()
    {
        const string handoff =
            "snssdk1233://x?params_url=https%3A%2F%2Fwww.tiktok.com%2Fa&b=2&c=3";

        Assert.Equal("https://www.tiktok.com/a", WebViewNavigationPolicy.ExtractHttpDestination(handoff));
    }

    [Fact]
    public void TheDestinationStopsAtAFragment()
    {
        const string handoff = "intent://x?params_url=https%3A%2F%2Fwww.tiktok.com%2Fb#Intent;end";

        Assert.Equal("https://www.tiktok.com/b", WebViewNavigationPolicy.ExtractHttpDestination(handoff));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("snssdk1233://webview")]
    [InlineData("snssdk1233://webview?other=1")]
    public void AHandOffWithNoDestinationIsNotFollowed(string? url)
    {
        // Cancelled stays cancelled rather than navigating somewhere unexpected.
        Assert.Null(WebViewNavigationPolicy.ExtractHttpDestination(url));
    }

    [Theory]
    [InlineData("snssdk1233://x?params_url=ftp%3A%2F%2Ffiles.example.com")]
    [InlineData("snssdk1233://x?params_url=somewhere-else")]
    public void ADestinationThatIsNotHttpIsRefused(string url)
    {
        Assert.Null(WebViewNavigationPolicy.ExtractHttpDestination(url));
    }

    [Fact]
    public void TheDestinationKeyIsMatchedCaseInsensitively()
    {
        const string handoff = "snssdk1233://x?PARAMS_URL=https%3A%2F%2Fwww.tiktok.com%2Fc";

        Assert.Equal("https://www.tiktok.com/c", WebViewNavigationPolicy.ExtractHttpDestination(handoff));
    }

    [Theory]
    [InlineData("https://www.tiktok.com/login")]
    [InlineData("https://www.tiktok.com/login/phone-or-email/email")]
    [InlineData("https://www.tiktok.com/LOGIN")]
    public void ASignInPageIsRecognised(string url)
    {
        // The importer relies on this to explain itself instead of waiting for a chat list that is
        // never coming.
        Assert.True(WebViewNavigationPolicy.IsLoginWall(url));
    }

    [Theory]
    [InlineData("https://www.tiktok.com/messages")]
    [InlineData("https://www.tiktok.com/foryou")]
    [InlineData("")]
    [InlineData(null)]
    public void AnOrdinaryPageIsNotASignInPage(string? url)
    {
        Assert.False(WebViewNavigationPolicy.IsLoginWall(url));
    }

    [Theory]
    [InlineData("https://www.tiktok.com/messages")]
    [InlineData("https://www.tiktok.com/messages?lang=en")]
    [InlineData("https://m.tiktok.com/messages")]
    [InlineData("https://www.tiktok.com/business-suite/messages")]
    [InlineData("https://www.tiktok.com/business-suite/messages?lang=id-ID&from=homepage")]
    public void TheInboxIsRecognised(string url)
    {
        // The reader is only ever sent here. Anything else and it reads a page that has no conversations
        // in it and reports a successful scan of an empty inbox.
        Assert.True(WebViewNavigationPolicy.IsInbox(url));
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("about:")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("https://www.tiktok.com/login")]
    [InlineData("https://www.tiktok.com/foryou")]
    [InlineData("data:text/html,hi")]
    public void AnythingElseIsNotTheInbox(string? url)
    {
        Assert.False(WebViewNavigationPolicy.IsInbox(url));
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("about:")]
    [InlineData("")]
    [InlineData(null)]
    public void ABlankPageIsRecognised(string? url)
    {
        // What a WebView shows before it has navigated anywhere, and after a load it could not perform.
        Assert.True(WebViewNavigationPolicy.IsBlank(url));
    }

    [Theory]
    [InlineData("https://www.tiktok.com/messages")]
    [InlineData("data:text/html,hi")]
    [InlineData("javascript:void(0)")]
    public void APageWithContentIsNotBlank(string url)
    {
        Assert.False(WebViewNavigationPolicy.IsBlank(url));
    }
}
