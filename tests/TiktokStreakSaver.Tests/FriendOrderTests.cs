using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// The send order is the saved friends list order, so these tests pin the list order rather than a
/// separate sort field.
/// </summary>
public class FriendOrderTests
{
    private static FriendConfig Entry(string id, bool enabled = true, bool isGroup = false) =>
        new() { Id = id, Username = id, DisplayName = id, IsEnabled = enabled, IsGroup = isGroup };

    private static string[] QueueIds(IEnumerable<FriendConfig>? all) =>
        FriendOrder.SendQueue(all).Select(f => f.Id).ToArray();

    [Fact]
    public void SendQueueKeepsTheSavedOrder()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b"), Entry("c") };

        Assert.Equal(new[] { "a", "b", "c" }, QueueIds(all));
    }

    [Fact]
    public void SendQueueDropsDisabledEntries()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b", enabled: false), Entry("c") };

        Assert.Equal(new[] { "a", "c" }, QueueIds(all));
    }

    [Fact]
    public void FriendsAndGroupsShareOneQueue()
    {
        // Storage is a single list; the Friends/Groups split is only a display concern. Which is
        // exactly why the send order has to be shown as one list.
        var all = new List<FriendConfig> { Entry("group", isGroup: true), Entry("friend") };

        Assert.Equal(new[] { "group", "friend" }, QueueIds(all));
    }

    [Fact]
    public void MoveUpSwapsWithTheEntryAboveIt()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b") };

        Assert.True(FriendOrder.TryMoveUp(all, "b"));

        Assert.Equal(new[] { "b", "a" }, QueueIds(all));
    }

    [Fact]
    public void MoveDownSwapsWithTheEntryBelowIt()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b") };

        Assert.True(FriendOrder.TryMoveDown(all, "a"));

        Assert.Equal(new[] { "b", "a" }, QueueIds(all));
    }

    [Fact]
    public void MovingTheFirstEntryUpReportsNoChange()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b") };

        Assert.False(FriendOrder.TryMoveUp(all, "a"));

        // Nothing moved: the caller disables the button instead of pretending it worked.
        Assert.Equal(new[] { "a", "b" }, QueueIds(all));
    }

    [Fact]
    public void MovingTheLastEntryDownReportsNoChange()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b") };

        Assert.False(FriendOrder.TryMoveDown(all, "b"));

        Assert.Equal(new[] { "a", "b" }, QueueIds(all));
    }

    [Fact]
    public void MoveSkipsDisabledEntriesSoTheQueueAlwaysVisiblyChanges()
    {
        // The trap: swapping with the disabled entry between them would reorder the stored list while
        // leaving the queue untouched, and the button would look broken.
        var all = new List<FriendConfig> { Entry("a"), Entry("off", enabled: false), Entry("b") };

        Assert.True(FriendOrder.TryMoveUp(all, "b"));

        Assert.Equal(new[] { "b", "a" }, QueueIds(all));
    }

    [Fact]
    public void MoveDownSkipsDisabledEntriesToo()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("off", enabled: false), Entry("b") };

        Assert.True(FriendOrder.TryMoveDown(all, "a"));

        Assert.Equal(new[] { "b", "a" }, QueueIds(all));
    }

    [Fact]
    public void ADisabledEntryAtTheTopDoesNotBlockTheFirstEnabledMoveUp()
    {
        var all = new List<FriendConfig> { Entry("off", enabled: false), Entry("a"), Entry("b") };

        Assert.False(FriendOrder.TryMoveUp(all, "a"));
        Assert.Equal(new[] { "a", "b" }, QueueIds(all));

        // ...but the entry below it can still be promoted to the front of the queue.
        Assert.True(FriendOrder.TryMoveUp(all, "b"));
        Assert.Equal(new[] { "b", "a" }, QueueIds(all));
    }

    [Fact]
    public void DisabledEntriesCannotBeMoved()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b", enabled: false) };

        Assert.False(FriendOrder.TryMoveUp(all, "b"));
        Assert.False(FriendOrder.TryMoveDown(all, "b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    public void UnknownTargetReportsNoChange(string? id)
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b") };

        Assert.False(FriendOrder.TryMoveUp(all, id));
        Assert.False(FriendOrder.TryMoveDown(all, id));
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void ANullListIsHandled()
    {
        Assert.Empty(FriendOrder.SendQueue(null));
        Assert.Equal(0, FriendOrder.CountInQueue(null));
        Assert.False(FriendOrder.TryMoveUp(null, "a"));
        Assert.False(FriendOrder.TryMoveDown(null, "a"));
    }

    [Fact]
    public void RepeatedMoveUpWalksAnEntryToTheFront()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b"), Entry("c"), Entry("d") };

        // Three taps take d to the front. Every tap must change something.
        Assert.True(FriendOrder.TryMoveUp(all, "d"));
        Assert.Equal(new[] { "a", "b", "d", "c" }, QueueIds(all));
        Assert.True(FriendOrder.TryMoveUp(all, "d"));
        Assert.Equal(new[] { "a", "d", "b", "c" }, QueueIds(all));
        Assert.True(FriendOrder.TryMoveUp(all, "d"));
        Assert.Equal(new[] { "d", "a", "b", "c" }, QueueIds(all));

        // Now it is first, so further taps are honestly refused.
        Assert.False(FriendOrder.TryMoveUp(all, "d"));
        Assert.Equal(new[] { "d", "a", "b", "c" }, QueueIds(all));
    }

    [Fact]
    public void MovingUpThenDownReturnsToTheOriginalOrder()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b"), Entry("c") };

        Assert.True(FriendOrder.TryMoveUp(all, "c"));
        Assert.Equal(new[] { "a", "c", "b" }, QueueIds(all));

        Assert.True(FriendOrder.TryMoveDown(all, "c"));
        Assert.Equal(new[] { "a", "b", "c" }, QueueIds(all));
    }

    [Fact]
    public void CountInQueueMatchesTheQueueLength()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("off", enabled: false), Entry("b") };

        Assert.Equal(2, FriendOrder.CountInQueue(all));
        Assert.Equal(FriendOrder.SendQueue(all).Count, FriendOrder.CountInQueue(all));
    }
}
