using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// The cooldown that decides whether a friend has already been messaged "this time".
/// <para>
/// The old rule compared the calendar day, which made a multi-time schedule useless: the first run of the
/// day claimed every friend and the later runs skipped all of them, while still reporting success. These
/// tests pin the replacement, because getting it wrong either breaks the feature again or starts sending
/// duplicates.
/// </para>
/// </summary>
public class SendCooldownTests
{
    private static readonly List<TimeSpan> ThreeTimes = new()
    {
        new TimeSpan(8, 0, 0),
        new TimeSpan(15, 0, 0),
        new TimeSpan(21, 0, 0),
    };

    /// <summary>
    /// The case the feature exists for: messaged at 08:00, due again at 15:00.
    /// </summary>
    [Fact]
    public void AFriendMessagedInAnEarlierSlotIsDueAgain()
    {
        var now = new DateTime(2026, 10, 26, 15, 2, 0);
        var slot = SendCooldown.SlotStart(true, ThreeTimes, null, now);

        Assert.Equal(new DateTime(2026, 10, 26, 15, 0, 0), slot);

        // Sent in the 08:00 slot, so the 15:00 slot has not messaged them yet.
        Assert.False(SendCooldown.AlreadySentForSlot(new DateTime(2026, 10, 26, 8, 0, 5), slot, now));
    }

    /// <summary>A friend already messaged inside the current slot is left alone.</summary>
    [Fact]
    public void AFriendMessagedInThisSlotIsSkipped()
    {
        // Late enough in the slot that the minimum-gap rule cannot be what decides it: this has to be the
        // slot comparison on its own.
        var now = new DateTime(2026, 10, 26, 15, 50, 0);
        var slot = SendCooldown.SlotStart(true, ThreeTimes, null, now);

        Assert.True(SendCooldown.AlreadySentForSlot(new DateTime(2026, 10, 26, 15, 3, 0), slot, now));
    }

    /// <summary>
    /// The slot opens at the latest configured time that has already arrived, not at the earliest.
    /// </summary>
    [Theory]
    [InlineData(7, 59, 21)]   // before the first time: still inside yesterday's 21:00 slot
    [InlineData(8, 0, 8)]
    [InlineData(12, 30, 8)]
    [InlineData(14, 59, 8)]
    [InlineData(15, 0, 15)]
    [InlineData(20, 59, 15)]
    [InlineData(21, 0, 21)]
    [InlineData(23, 59, 21)]
    public void TheSlotOpensAtTheLatestTimeThatHasArrived(int hour, int minute, int expectedHour)
    {
        var now = new DateTime(2026, 10, 26, hour, minute, 0);
        var slot = SendCooldown.SlotStart(true, ThreeTimes, null, now);

        Assert.Equal(expectedHour, slot.Hour);
    }

    /// <summary>
    /// Just after midnight the open slot is the last one from the day before. Treating the new day as
    /// untouched would let a run that fires at 00:05 message everyone again.
    /// </summary>
    [Fact]
    public void BeforeTheFirstTimeOfDayTheSlotIsYesterdaysLast()
    {
        var now = new DateTime(2026, 10, 26, 0, 5, 0);
        var slot = SendCooldown.SlotStart(true, ThreeTimes, null, now);

        Assert.Equal(new DateTime(2026, 10, 25, 21, 0, 0), slot);
    }

    /// <summary>
    /// A single daily time behaves the same way: yesterday's send does not block today's.
    /// </summary>
    [Fact]
    public void ASingleDailyTimeStillRepeatsEveryDay()
    {
        var times = new List<TimeSpan> { new(9, 0, 0) };
        var now = new DateTime(2026, 10, 26, 9, 1, 0);
        var slot = SendCooldown.SlotStart(true, times, null, now);

        Assert.Equal(new DateTime(2026, 10, 26, 9, 0, 0), slot);
        Assert.False(SendCooldown.AlreadySentForSlot(new DateTime(2026, 10, 25, 9, 0, 5), slot, now));
        Assert.True(SendCooldown.AlreadySentForSlot(new DateTime(2026, 10, 26, 9, 0, 5), slot, now));
    }

    /// <summary>
    /// With no times configured the schedule behaves as a single midnight run, matching
    /// <see cref="ScheduleCalculator.NextDailyTimes"/> rather than skipping everything.
    /// </summary>
    [Fact]
    public void NoConfiguredTimesMeansASingleMidnightSlot()
    {
        var now = new DateTime(2026, 10, 26, 13, 0, 0);
        var slot = SendCooldown.SlotStart(true, new List<TimeSpan>(), null, now);

        Assert.Equal(new DateTime(2026, 10, 26, 0, 0, 0), slot);
    }

    /// <summary>
    /// The interval schedule has no clock times, so the slot opens with the previous run - the same event
    /// the interval is measured from.
    /// </summary>
    [Fact]
    public void AnIntervalScheduleOpensItsSlotWithThePreviousRun()
    {
        var lastRun = new DateTime(2026, 10, 26, 8, 0, 0);
        var now = new DateTime(2026, 10, 26, 23, 0, 0);

        var slot = SendCooldown.SlotStart(false, null, lastRun, now);

        Assert.Equal(lastRun, slot);
        Assert.True(SendCooldown.AlreadySentForSlot(new DateTime(2026, 10, 26, 8, 0, 5), slot, now));
    }

    /// <summary>A first run, with nothing behind it, has nobody due to be skipped.</summary>
    [Fact]
    public void WithNoPreviousRunNobodyIsSkipped()
    {
        var now = new DateTime(2026, 10, 26, 8, 0, 0);
        var slot = SendCooldown.SlotStart(false, null, null, now);

        Assert.False(SendCooldown.AlreadySentForSlot(null, slot, now));
    }

    /// <summary>
    /// The minimum gap holds even when the slot rule would allow the send, so two slots configured close
    /// together cannot produce two messages back to back.
    /// </summary>
    [Fact]
    public void TheMinimumGapHoldsAcrossSlots()
    {
        var times = new List<TimeSpan> { new(8, 0, 0), new(8, 10, 0) };
        var now = new DateTime(2026, 10, 26, 8, 11, 0);
        var slot = SendCooldown.SlotStart(true, times, null, now);

        // The 08:10 slot is open, but the send at 08:05 was only six minutes ago.
        Assert.True(SendCooldown.AlreadySentForSlot(new DateTime(2026, 10, 26, 8, 5, 0), slot, now));
    }

    /// <summary>Once the gap has passed, a new slot is free to send again.</summary>
    [Fact]
    public void AfterTheMinimumGapANewSlotIsFree()
    {
        var now = new DateTime(2026, 10, 26, 15, 2, 0);
        var slot = SendCooldown.SlotStart(true, ThreeTimes, null, now);

        var sentLongAgo = now.AddMinutes(-SendCooldown.MinimumGapMinutes - 1);
        Assert.False(SendCooldown.AlreadySentForSlot(sentLongAgo, slot, now));
    }

    /// <summary>
    /// Nothing recorded means nothing to skip. A friend who has never been messaged is always due.
    /// </summary>
    [Fact]
    public void AFriendNeverMessagedIsNeverSkipped()
    {
        var now = new DateTime(2026, 10, 26, 15, 0, 0);
        var slot = SendCooldown.SlotStart(true, ThreeTimes, null, now);

        Assert.False(SendCooldown.AlreadySentForSlot(null, slot, now));
    }
}
