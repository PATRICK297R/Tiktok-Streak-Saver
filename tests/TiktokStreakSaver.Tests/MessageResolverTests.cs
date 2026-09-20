using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class MessageResolverTests
{
    private const string Shared = "shared message";
    private const string Randomized = "random variant";
    private const string PerFriend = "hey buddy";

    [Fact]
    public void PerFriendMessageWinsOverEverything()
    {
        // The most specific thing the user typed must not be overruled by a generic variant pool.
        var resolved = MessageResolver.Resolve(PerFriend, Randomized, Shared);

        Assert.Equal(PerFriend, resolved);
    }

    [Fact]
    public void RandomVariantIsUsedWhenTheFriendHasNoOwnMessage()
    {
        Assert.Equal(Randomized, MessageResolver.Resolve(null, Randomized, Shared));
    }

    [Fact]
    public void SharedMessageIsTheFinalFallback()
    {
        Assert.Equal(Shared, MessageResolver.Resolve(null, null, Shared));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void BlankCustomMessageMeansUseTheSharedOne(string? custom)
    {
        Assert.Equal(Shared, MessageResolver.Resolve(custom, null, Shared));
        Assert.False(MessageResolver.IsOverride(custom));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankRandomizedVariantFallsThroughToShared(string? randomized)
    {
        Assert.Equal(Shared, MessageResolver.Resolve(null, randomized, Shared));
    }

    [Fact]
    public void MatchesTheBehaviourTheServiceUsedBefore()
    {
        // Regression guard: with no per-friend message and no pool, the outcome must be exactly what
        // the old if/else produced, so existing installs see no change.
        Assert.Equal(Shared, MessageResolver.Resolve(null, null, Shared));

        // And with a pool active, the variant must still be what gets sent.
        Assert.Equal(Randomized, MessageResolver.Resolve(null, Randomized, Shared));
    }

    [Fact]
    public void NormalizeTrimsAndCollapsesBlankToNull()
    {
        Assert.Equal("hey buddy", MessageResolver.Normalize("  hey buddy  "));
        Assert.Null(MessageResolver.Normalize(null));
        Assert.Null(MessageResolver.Normalize(""));
        Assert.Null(MessageResolver.Normalize("   \n "));
    }

    [Fact]
    public void NormalizeKeepsInnerFormattingIntact()
    {
        // Only the edges are trimmed; the user's own line breaks must survive.
        Assert.Equal("line one\nline two", MessageResolver.Normalize("  line one\nline two  "));
    }

    [Fact]
    public void HasCustomMessageReflectsTheStoredValue()
    {
        var friend = new FriendConfig { Username = "ana" };
        Assert.False(friend.HasCustomMessage);

        friend.CustomMessage = "hey";
        Assert.True(friend.HasCustomMessage);

        friend.CustomMessage = "   ";
        Assert.False(friend.HasCustomMessage);

        friend.CustomMessage = null;
        Assert.False(friend.HasCustomMessage);
    }

    [Fact]
    public void PlaceholdersStillApplyToAPerFriendMessage()
    {
        // The two features compose: a per-friend message can also address the friend by name.
        var resolved = MessageResolver.Resolve("hi {name}!", null, Shared);
        var rendered = MessageTemplate.Render(resolved, "ana", "Ana Bella");

        Assert.Equal("hi Ana Bella!", rendered);
    }
}
