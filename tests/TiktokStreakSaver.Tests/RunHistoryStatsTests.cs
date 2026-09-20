using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class RunHistoryStatsTests
{
    private static readonly DateTime Today = new(2026, 9, 17, 12, 0, 0);

    private static StreakRunResult Run(DateTime when, int sent, int failed) =>
        new()
        {
            RunTime = when,
            FriendResults = Enumerable.Range(0, sent)
                .Select(i => new FriendMessageResult { FriendId = $"s{i}", Username = $"s{i}", Success = true })
                .Concat(Enumerable.Range(0, failed)
                    .Select(i => new FriendMessageResult { FriendId = $"f{i}", Username = $"f{i}", Success = false, ErrorMessage = "x" }))
                .ToList()
        };

    [Fact]
    public void CoversTodayPlusThePrecedingDays()
    {
        // 7 days means today and the six before it.
        var history = new List<StreakRunResult>
        {
            Run(Today, 1, 0),                      // day 0, inside
            Run(Today.AddDays(-6), 1, 0),          // oldest day inside
            Run(Today.AddDays(-7), 1, 0),          // one day too old
        };

        var stats = RunHistoryStats.ForLastDays(history, 7, Today);

        Assert.Equal(7, stats.Days);
        Assert.Equal(2, stats.Runs);
        Assert.Equal(2, stats.Messages);
        Assert.Equal(2, stats.DaysWithRuns);
    }

    [Fact]
    public void IgnoresRunsDatedInTheFuture()
    {
        var history = new List<StreakRunResult> { Run(Today, 1, 0), Run(Today.AddDays(1), 5, 0) };

        var stats = RunHistoryStats.ForLastDays(history, 7, Today);

        Assert.Equal(1, stats.Runs);
        Assert.Equal(1, stats.Messages);
    }

    [Fact]
    public void SumsFailedAttemptsIntoTheRate()
    {
        var history = new List<StreakRunResult> { Run(Today, 3, 1), Run(Today.AddDays(-1), 1, 0) };

        var stats = RunHistoryStats.ForLastDays(history, 7, Today);

        Assert.Equal(5, stats.Messages);
        Assert.Equal(4, stats.SuccessfulMessages);
        Assert.Equal(80, stats.SuccessPercent);
        Assert.True(stats.HasData);
    }

    [Fact]
    public void CountsSeveralRunsOnTheSameDayAsOneActiveDay()
    {
        var history = new List<StreakRunResult>
        {
            Run(Today, 1, 0),
            Run(Today.AddHours(-2), 1, 0),
            Run(Today.AddHours(-4), 1, 0),
        };

        var stats = RunHistoryStats.ForLastDays(history, 7, Today);

        Assert.Equal(3, stats.Runs);
        Assert.Equal(1, stats.DaysWithRuns);
    }

    [Fact]
    public void SingleDayWindowOnlyCountsToday()
    {
        var history = new List<StreakRunResult> { Run(Today, 2, 0), Run(Today.AddDays(-1), 9, 0) };

        var stats = RunHistoryStats.ForLastDays(history, 1, Today);

        Assert.Equal(1, stats.Days);
        Assert.Equal(1, stats.Runs);
        Assert.Equal(2, stats.Messages);
    }

    [Fact]
    public void HandlesEmptyAndMissingHistory()
    {
        var empty = RunHistoryStats.ForLastDays(new List<StreakRunResult>(), 7, Today);
        Assert.Equal(0, empty.Runs);
        Assert.Equal(0, empty.SuccessPercent);
        Assert.False(empty.HasData);

        var missing = RunHistoryStats.ForLastDays(null, 7, Today);
        Assert.Equal(0, missing.Runs);
        Assert.False(missing.HasData);
    }

    [Fact]
    public void TreatsRunWithoutPerFriendResultsAsAnActiveDayOnly()
    {
        // An all-skipped run carries no results at all. It still counts as a run, but contributes
        // nothing to the message totals.
        var history = new List<StreakRunResult> { new() { RunTime = Today, FriendResults = new() } };

        var stats = RunHistoryStats.ForLastDays(history, 7, Today);

        Assert.Equal(1, stats.Runs);
        Assert.Equal(1, stats.DaysWithRuns);
        Assert.Equal(0, stats.Messages);
        Assert.Equal(0, stats.SuccessPercent);
        Assert.True(stats.HasData);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void ClampsTheWindowToAtLeastOneDay(int requested, int expectedDays)
    {
        var stats = RunHistoryStats.ForLastDays(new List<StreakRunResult>(), requested, Today);

        Assert.Equal(expectedDays, stats.Days);
    }
}
