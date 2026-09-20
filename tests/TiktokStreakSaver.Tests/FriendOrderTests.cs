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

    // ─── Dropping one row onto another (drag and drop) ───────────────────────

    [Fact]
    public void DroppingAnEntryOnOneBelowItTakesThatPlace()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b"), Entry("c"), Entry("d") };

        // a lands where c was, and everything between them shifts up to make room.
        Assert.True(FriendOrder.TryMoveTo(all, "a", "c"));

        Assert.Equal(new[] { "b", "c", "a", "d" }, QueueIds(all));
    }

    [Fact]
    public void DroppingAnEntryOnOneAboveItTakesThatPlace()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b"), Entry("c"), Entry("d") };

        Assert.True(FriendOrder.TryMoveTo(all, "b", "d"));

        Assert.Equal(new[] { "a", "c", "d", "b" }, QueueIds(all));
    }

    [Fact]
    public void DroppingAnEntryOnItselfChangesNothing()
    {
        // Dropping a row back where it came from is the commonest way to end a drag, so it has to be
        // reported as no change rather than saved as a move.
        var all = new List<FriendConfig> { Entry("a"), Entry("b") };

        Assert.False(FriendOrder.TryMoveTo(all, "a", "a"));
        Assert.Equal(new[] { "a", "b" }, QueueIds(all));
    }

    [Fact]
    public void DroppingOntoADisabledEntryChangesNothing()
    {
        // A disabled entry is not in the queue, so it is not on screen to be dropped on. Refusing rather
        // than guessing keeps drag and the arrows from disagreeing about what a move means.
        var all = new List<FriendConfig> { Entry("a"), Entry("off", enabled: false), Entry("b") };

        Assert.False(FriendOrder.TryMoveTo(all, "a", "off"));
        Assert.Equal(new[] { "a", "b" }, QueueIds(all));
    }

    [Fact]
    public void ADisabledEntryCannotBeDragged()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("off", enabled: false) };

        Assert.False(FriendOrder.TryMoveTo(all, "off", "a"));
        Assert.Equal(new[] { "a" }, QueueIds(all));
    }

    [Fact]
    public void DroppingLeavesDisabledEntriesInTheSlotsTheyAreIn()
    {
        var all = new List<FriendConfig>
        {
            Entry("a"), Entry("off1", enabled: false), Entry("b"), Entry("off2", enabled: false)
        };

        Assert.True(FriendOrder.TryMoveTo(all, "b", "a"));

        Assert.Equal(new[] { "b", "a" }, QueueIds(all));

        // Same objects in the same slots: only the enabled ones were refilled around them.
        Assert.Equal("b", all[0].Id);
        Assert.Equal("off1", all[1].Id);
        Assert.Equal("a", all[2].Id);
        Assert.Equal("off2", all[3].Id);
    }

    [Fact]
    public void AnyOrderCanBeBuiltByDropping()
    {
        // Reverses the queue by repeatedly dragging the last entry onto the first. This is the move the
        // arrows need three presses for, and the reason dragging is worth having.
        var all = new List<FriendConfig> { Entry("a"), Entry("b"), Entry("c"), Entry("d") };

        Assert.True(FriendOrder.TryMoveTo(all, "d", "a"));
        Assert.Equal(new[] { "d", "a", "b", "c" }, QueueIds(all));

        Assert.True(FriendOrder.TryMoveTo(all, "c", "a"));
        Assert.Equal(new[] { "d", "c", "a", "b" }, QueueIds(all));

        Assert.True(FriendOrder.TryMoveTo(all, "b", "a"));
        Assert.Equal(new[] { "d", "c", "b", "a" }, QueueIds(all));
    }

    [Fact]
    public void ADropNeverLosesOrDuplicatesAnEntry()
    {
        // The whole list is rewritten from the queue order, so a mistake here would drop an entry or
        // write one twice - the kind of damage a reorder must never do.
        var all = new List<FriendConfig>
        {
            Entry("a"), Entry("b"), Entry("off", enabled: false), Entry("c"), Entry("d")
        };

        Assert.True(FriendOrder.TryMoveTo(all, "d", "a"));

        Assert.Equal(5, all.Count);
        Assert.Equal(new[] { "a", "b", "c", "d", "off" }, all.Select(f => f.Id).OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "d", "a", "b", "c" }, QueueIds(all));
    }

    [Theory]
    [InlineData(null, "a")]
    [InlineData("", "a")]
    [InlineData("missing", "a")]
    [InlineData("a", null)]
    [InlineData("a", "")]
    [InlineData("a", "missing")]
    public void UnknownDropTargetsReportNoChange(string? id, string? targetId)
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b") };

        Assert.False(FriendOrder.TryMoveTo(all, id, targetId));
        Assert.Equal(new[] { "a", "b" }, QueueIds(all));
    }

    [Fact]
    public void DroppingOnANullListIsHandled()
    {
        Assert.False(FriendOrder.TryMoveTo(null, "a", "b"));
    }

    // ─── Rearranging one section of the list (drag inside Friends or Groups) ──

    [Fact]
    public void MovingWithinASectionTakesTheTargetsPlace()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b"), Entry("c"), Entry("d") };

        Assert.True(FriendOrder.TryMoveWithinSection(all, "a", "c"));

        Assert.Equal(new[] { "b", "c", "a", "d" }, all.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void MovingWithinASectionLeavesTheOtherKindWhereItWas()
    {
        // The list is one sequence split into two sections for display. Rearranging the friends must not
        // shuffle the groups, which is the whole reason this is not a queue-wide move.
        var all = new List<FriendConfig>
        {
            Entry("f1"), Entry("g1", isGroup: true), Entry("f2"), Entry("g2", isGroup: true), Entry("f3")
        };

        Assert.True(FriendOrder.TryMoveWithinSection(all, "f3", "f1"));

        Assert.Equal(new[] { "f3", "g1", "f1", "g2", "f2" }, all.Select(f => f.Id).ToArray());

        // Both groups are in the slots they started in.
        Assert.Equal(1, all.FindIndex(f => f.Id == "g1"));
        Assert.Equal(3, all.FindIndex(f => f.Id == "g2"));
    }

    [Fact]
    public void RearrangingGroupsLeavesTheFriendsWhereTheyWere()
    {
        var all = new List<FriendConfig>
        {
            Entry("f1"), Entry("g1", isGroup: true), Entry("f2"), Entry("g2", isGroup: true)
        };

        Assert.True(FriendOrder.TryMoveWithinSection(all, "g2", "g1"));

        Assert.Equal(new[] { "f1", "g2", "f2", "g1" }, all.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void DisabledEntriesCanBeMovedWithinASection()
    {
        // Unlike the queue, the list shows disabled entries with their switch off. A row the user can see
        // and press has to move when they move it, or the handle reads as broken.
        var all = new List<FriendConfig>
        {
            Entry("a"), Entry("off", enabled: false), Entry("b")
        };

        Assert.True(FriendOrder.TryMoveWithinSection(all, "off", "a"));

        Assert.Equal(new[] { "off", "a", "b" }, all.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void AnEnabledEntryCanBeDroppedOnADisabledOne()
    {
        var all = new List<FriendConfig>
        {
            Entry("a"), Entry("off", enabled: false), Entry("b")
        };

        Assert.True(FriendOrder.TryMoveWithinSection(all, "b", "off"));

        Assert.Equal(new[] { "a", "b", "off" }, all.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void DroppingAFriendOnAGroupChangesNothing()
    {
        // They are in different sections, so one of these ids came from somewhere the drag did not.
        var all = new List<FriendConfig> { Entry("f"), Entry("g", isGroup: true) };

        Assert.False(FriendOrder.TryMoveWithinSection(all, "f", "g"));
        Assert.Equal(new[] { "f", "g" }, all.Select(x => x.Id).ToArray());
    }

    [Fact]
    public void DroppingAnEntryOnItselfInTheListChangesNothing()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b") };

        Assert.False(FriendOrder.TryMoveWithinSection(all, "a", "a"));
        Assert.Equal(new[] { "a", "b" }, all.Select(f => f.Id).ToArray());
    }

    [Theory]
    [InlineData(null, "a")]
    [InlineData("", "a")]
    [InlineData("missing", "a")]
    [InlineData("a", null)]
    [InlineData("a", "")]
    [InlineData("a", "missing")]
    public void UnknownSectionTargetsReportNoChange(string? id, string? targetId)
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b") };

        Assert.False(FriendOrder.TryMoveWithinSection(all, id, targetId));
        Assert.Equal(new[] { "a", "b" }, all.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void MovingWithinASectionNeverLosesOrDuplicatesAnEntry()
    {
        var all = new List<FriendConfig>
        {
            Entry("a"), Entry("g", isGroup: true), Entry("off", enabled: false), Entry("b")
        };

        Assert.True(FriendOrder.TryMoveWithinSection(all, "b", "off"));

        Assert.Equal(4, all.Count);
        Assert.Equal(new[] { "a", "b", "g", "off" }, all.Select(f => f.Id).OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "a", "b", "off" }, all.Where(f => !f.IsGroup).Select(f => f.Id).ToArray());
    }

    [Fact]
    public void AnySectionOrderCanBeBuiltByDropping()
    {
        var all = new List<FriendConfig> { Entry("a"), Entry("b"), Entry("c"), Entry("d") };

        Assert.True(FriendOrder.TryMoveWithinSection(all, "d", "a"));
        Assert.Equal(new[] { "d", "a", "b", "c" }, all.Select(f => f.Id).ToArray());

        Assert.True(FriendOrder.TryMoveWithinSection(all, "c", "a"));
        Assert.Equal(new[] { "d", "c", "a", "b" }, all.Select(f => f.Id).ToArray());

        Assert.True(FriendOrder.TryMoveWithinSection(all, "b", "a"));
        Assert.Equal(new[] { "d", "c", "b", "a" }, all.Select(f => f.Id).ToArray());
    }
}
