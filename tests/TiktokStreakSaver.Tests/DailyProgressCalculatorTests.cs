using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class DailyProgressCalculatorTests
{
    private static readonly DateTime Today = new(2026, 9, 17, 14, 30, 0);
    private static readonly DateTime Yesterday = Today.AddDays(-1);

    private static StreakRunResult Run(DateTime when, params FriendMessageResult[] results) =>
        new() { RunTime = when, FriendResults = results.ToList() };

    private static FriendMessageResult Sent(string id, string username) =>
        new() { FriendId = id, Username = username, Success = true };

    private static FriendMessageResult Failed(string id, string username) =>
        new() { FriendId = id, Username = username, Success = false, ErrorMessage = "not found" };

    [Fact]
    public void KeepsTodaysProgressWhenTheNewestRunOnlySkippedEveryone()
    {
        // Regression guard. A friend already messaged today is skipped by later runs, and such a run
        // is recorded with no per-friend results at all. Reading only history[0] therefore reported
        // "0 sent" for a day that had already succeeded - the Daily Automation Progress card reset
        // itself after every extra run.
        var history = new List<StreakRunResult>
        {
            Run(Today.AddHours(1)),                                    // newest: all skipped, no results
            Run(Today, Sent("a", "ana"), Sent("b", "budi"), Sent("c", "citra")),
        };

        var progress = DailyProgressCalculator.Calculate(history, enabledFriendCount: 3, today: Today);

        Assert.True(progress.RanToday);
        Assert.Equal(3, progress.SentToday);
        Assert.Equal(3, progress.Attempted);
        Assert.Equal(100, progress.SuccessPercent);
        Assert.Equal(0, progress.Remaining);
        Assert.Equal(1f, progress.Fraction);
        Assert.Equal("3/3", progress.ProgressText);
    }

    [Fact]
    public void CountsEachFriendOnceAcrossOverlappingRuns()
    {
        var history = new List<StreakRunResult>
        {
            Run(Today.AddHours(5), Sent("a", "ana")),                  // retry re-sent ana
            Run(Today.AddHours(2), Sent("a", "ana"), Failed("b", "budi")),
        };

        var progress = DailyProgressCalculator.Calculate(history, enabledFriendCount: 2, today: Today);

        Assert.Equal(1, progress.SentToday);
        Assert.Equal(2, progress.Attempted);
        Assert.Equal(50, progress.SuccessPercent);
        Assert.Equal(1, progress.Remaining);
    }

    [Fact]
    public void IgnoresRunsFromOtherDays()
    {
        var history = new List<StreakRunResult> { Run(Yesterday, Sent("a", "ana")) };

        var progress = DailyProgressCalculator.Calculate(history, enabledFriendCount: 2, today: Today);

        Assert.False(progress.RanToday);
        Assert.Equal(0, progress.SentToday);
        Assert.Equal(2, progress.Expected);
        Assert.Equal(0f, progress.Fraction);
        Assert.Equal("0/2", progress.ProgressText);
    }

    [Fact]
    public void ExpectedNeverFallsBelowSentWhenAFriendIsRemovedAfterwards()
    {
        // Otherwise the card would read "3/1", which looks like a bug to the user.
        var history = new List<StreakRunResult>
        {
            Run(Today, Sent("a", "ana"), Sent("b", "budi"), Sent("c", "citra")),
        };

        var progress = DailyProgressCalculator.Calculate(history, enabledFriendCount: 1, today: Today);

        Assert.Equal(3, progress.Expected);
        Assert.Equal(0, progress.Remaining);
        Assert.Equal("3/3", progress.ProgressText);
    }

    [Fact]
    public void FallsBackToUsernameWhenFriendIdWasNotRecorded()
    {
        var history = new List<StreakRunResult>
        {
            Run(Today, new FriendMessageResult { FriendId = "", Username = "ana", Success = true }),
            Run(Today.AddHours(1), new FriendMessageResult { FriendId = "", Username = "ana", Success = true }),
        };

        var progress = DailyProgressCalculator.Calculate(history, enabledFriendCount: 1, today: Today);

        Assert.Equal(1, progress.SentToday);
        Assert.Equal(1, progress.Attempted);
    }

    [Fact]
    public void IgnoresResultsThatCarryNoIdentityAtAll()
    {
        var history = new List<StreakRunResult>
        {
            Run(Today, new FriendMessageResult { FriendId = "", Username = "", Success = true }),
        };

        var progress = DailyProgressCalculator.Calculate(history, enabledFriendCount: 1, today: Today);

        Assert.True(progress.RanToday);
        Assert.Equal(0, progress.SentToday);
        Assert.Equal(0, progress.Attempted);
        Assert.Equal(0, progress.SuccessPercent);
    }

    [Fact]
    public void HandlesMissingHistoryAndHistoryWithoutRuns()
    {
        var noHistory = DailyProgressCalculator.Calculate(null, enabledFriendCount: 4, today: Today);
        Assert.False(noHistory.RanToday);
        Assert.Equal("0/4", noHistory.ProgressText);

        var noRuns = DailyProgressCalculator.Calculate(new List<StreakRunResult>(), 0, Today);
        Assert.False(noRuns.RanToday);
        Assert.Equal("0/0", noRuns.ProgressText);
        Assert.Equal(0f, noRuns.Fraction);
        Assert.Equal(0, noRuns.SuccessPercent);
    }
}
