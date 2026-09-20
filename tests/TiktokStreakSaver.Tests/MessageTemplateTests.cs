using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class MessageTemplateTests
{
    [Fact]
    public void ReplacesTheUsernameToken()
    {
        var rendered = MessageTemplate.Render("hey {username} 🔥", "ana", "Ana Bella");

        Assert.Equal("hey ana 🔥", rendered);
    }

    [Fact]
    public void PrefersTheDisplayNameForTheNameToken()
    {
        var rendered = MessageTemplate.Render("hi {name}", "ana", "Ana Bella");

        Assert.Equal("hi Ana Bella", rendered);
    }

    [Fact]
    public void NameTokenFallsBackToTheUsernameWhenNoDisplayNameIsSet()
    {
        var rendered = MessageTemplate.Render("hi {name}", "ana", "");

        Assert.Equal("hi ana", rendered);
    }

    [Fact]
    public void UsernameTokenFallsBackToTheDisplayNameForGroups()
    {
        // Group chats have no @handle. Without the fallback this would send "hey " with a hole in it.
        var rendered = MessageTemplate.Render("hey {username}", "", "Study Group");

        Assert.Equal("hey Study Group", rendered);
    }

    [Fact]
    public void MatchesTokensRegardlessOfCase()
    {
        var rendered = MessageTemplate.Render("{NAME} / {UserName}", "ana", "Ana Bella");

        Assert.Equal("Ana Bella / ana", rendered);
    }

    [Fact]
    public void ReplacesEveryOccurrence()
    {
        var rendered = MessageTemplate.Render("{username}{username} {name}", "ana", "Ana Bella");

        Assert.Equal("anaana Ana Bella", rendered);
    }

    [Fact]
    public void LeavesUnknownTokensAlone()
    {
        var rendered = MessageTemplate.Render("keep {our} streak {username}", "ana", "Ana Bella");

        Assert.Equal("keep {our} streak ana", rendered);
    }

    [Fact]
    public void ReturnsTextWithoutTokensUnchanged()
    {
        Assert.Equal("plain streak message", MessageTemplate.Render("plain streak message", "ana", "Ana"));
        Assert.Equal("{}", MessageTemplate.Render("{}", "ana", "Ana"));
    }

    [Fact]
    public void HandlesMissingInput()
    {
        Assert.Equal(string.Empty, MessageTemplate.Render(null, "ana", "Ana"));
        Assert.Equal(string.Empty, MessageTemplate.Render("", "ana", "Ana"));
    }

    [Fact]
    public void HandlesMissingFriendDetailsWithoutLeavingHoles()
    {
        // Both values empty: the tokens must not survive into the sent message.
        Assert.Equal("hi ", MessageTemplate.Render("hi {name}", null, null));
        Assert.Equal("hi ", MessageTemplate.Render("hi {username}", null, null));
    }
}
