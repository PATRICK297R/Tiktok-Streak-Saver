using System.Text.Json;
using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// Progress is worked out from run history, and run history comes back off disk. These tests go through
/// the app's own serialiser rather than handing the calculator an object it never sees in practice - the
/// difference matters, because the round trip is where the run's timestamp can change meaning.
/// </summary>
public class RunHistoryStorageTests
{
    /// <summary>The same path <c>SettingsService.AddRunResult</c> then <c>GetRunHistory</c> use.</summary>
    private static List<StreakRunResult> RoundTrip(IEnumerable<StreakRunResult> history)
    {
        var json = JsonSerializer.Serialize(history.ToList(), AppJsonContext.Default.ListStreakRunResult);
        return JsonSerializer.Deserialize(json, AppJsonContext.Default.ListStreakRunResult) ?? new();
    }

    private static StreakRunResult MorningRun(DateTime when) => new()
    {
        RunTime = when,
        Success = true,
        FriendResults = Enumerable.Range(1, 11)
            .Select(i => new FriendMessageResult { FriendId = $"id{i}", Username = $"user{i}", Success = true })
            .ToList()
    };

    /// <summary>
    /// The run happens on a schedule, and a morning schedule is the common case. The date the history
    /// carries has to still be the local date it happened on after being written and read back, or the
    /// day's progress is filed under the previous day.
    /// </summary>
    [Fact]
    public void ARunFromEarlyThisMorningStillCountsAsTodayAfterBeingStored()
    {
        var morning = new DateTime(2026, 9, 18, 6, 0, 0, DateTimeKind.Local);

        var progress = DailyProgressCalculator.Calculate(
            RoundTrip(new[] { MorningRun(morning) }),
            enabledFriendCount: 11,
            today: new DateTime(2026, 9, 18));

        Assert.True(progress.RanToday);
        Assert.Equal(11, progress.SentToday);
        Assert.Equal("11/11", progress.ProgressText);
    }

    /// <summary>
    /// The other half of the same defect, and the more expensive one: the send loop skips a friend who was
    /// already messaged today by comparing the stored date with the local one. Read back as a UTC instant, a
    /// message sent at 06:00 local looked like yesterday's, so the friend was not recognised as done and was
    /// messaged a second time.
    /// </summary>
    [Fact]
    public void AMessageSentThisMorningStillReadsAsSentToday()
    {
        var sentAt = new DateTime(2026, 9, 18, 6, 0, 0, DateTimeKind.Local);
        var friend = new FriendConfig { Username = "ana", LastMessageSent = sentAt };

        var json = JsonSerializer.Serialize(friend, AppJsonContext.Default.FriendConfig);
        var stored = JsonSerializer.Deserialize(json, AppJsonContext.Default.FriendConfig)!;

        Assert.Equal(new DateTime(2026, 9, 18), stored.LastMessageSent!.Value.Date);
        Assert.Equal(sentAt, stored.LastMessageSent.Value);
    }

    [Fact]
    public void ARunFromLateEveningStillCountsAsTodayAfterBeingStored()
    {
        var evening = new DateTime(2026, 9, 18, 23, 30, 0, DateTimeKind.Local);

        var progress = DailyProgressCalculator.Calculate(
            RoundTrip(new[] { MorningRun(evening) }),
            enabledFriendCount: 11,
            today: new DateTime(2026, 9, 18));

        Assert.Equal(11, progress.SentToday);
    }

    /// <summary>
    /// The other side of the same rule: a run from yesterday must not be counted as today's, whatever
    /// hour it happened at. Counting it would let yesterday's success hide a day that never ran.
    /// </summary>
    [Fact]
    public void ARunFromYesterdayIsNotCountedAsToday()
    {
        var yesterdayEvening = new DateTime(2026, 9, 17, 23, 30, 0, DateTimeKind.Local);

        var progress = DailyProgressCalculator.Calculate(
            RoundTrip(new[] { MorningRun(yesterdayEvening) }),
            enabledFriendCount: 11,
            today: new DateTime(2026, 9, 18));

        Assert.False(progress.RanToday);
        Assert.Equal(0, progress.SentToday);
    }

    [Fact]
    public void TheStoredTimestampReadsBackAsTheSameMomentInTime()
    {
        var when = new DateTime(2026, 9, 18, 6, 0, 0, DateTimeKind.Local);

        var stored = RoundTrip(new[] { MorningRun(when) })[0];

        // Compared as instants, which is the only comparison that survives a change of Kind.
        Assert.Equal(when.ToUniversalTime(), stored.RunTime.ToUniversalTime());

        // And back as a local time, which is what every question the app asks about a run assumes. A UTC
        // value here is the defect that filed morning runs under the previous day.
        Assert.Equal(DateTimeKind.Local, stored.RunTime.Kind);
        Assert.Equal(when.Date, stored.RunTime.Date);
    }

    /// <summary>
    /// The weekly figures group runs by the day they happened, and they read the same stored timestamps.
    /// A run filed a day early would drop out of the window at the wrong end.
    /// </summary>
    [Fact]
    public void TheWeeklyStatsAgreeWithTheDayTheRunHappened()
    {
        var morning = new DateTime(2026, 9, 18, 6, 0, 0, DateTimeKind.Local);

        var week = RunHistoryStats.ForLastDays(
            RoundTrip(new[] { MorningRun(morning) }),
            days: 7,
            today: new DateTime(2026, 9, 18));

        Assert.Equal(1, week.Runs);
        Assert.Equal(11, week.SuccessfulMessages);
    }
}
