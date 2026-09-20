using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// A dry run walks the automation without sending. Everything that reports "sent today" or a success
/// rate must therefore ignore its rows, or a rehearsal would look like a finished day and silence the
/// at-risk warning.
/// </summary>
public class DryRunExclusionTests
{
    private static readonly DateTime Today = new(2026, 9, 17, 12, 0, 0);

    /// <summary>22:00 on the same day. Not Today.AddHours(22) - that lands on the next day.</summary>
    private static readonly DateTime LateEvening = new(2026, 9, 17, 22, 0, 0);

    [Fact]
    public void DailyProgressIgnoresDryRunResults()
    {
        var history = new List<StreakRunResult>
        {
            new()
            {
                RunTime = Today,
                DryRun = true,
                FriendResults = new List<FriendMessageResult>
                {
                    new() { FriendId = "a", Username = "ana", Success = true, DryRun = true },
                    new() { FriendId = "b", Username = "budi", Success = true, DryRun = true },
                },
            },
        };

        var progress = DailyProgressCalculator.Calculate(history, enabledFriendCount: 2, today: Today);

        Assert.Equal(0, progress.SentToday);
        Assert.Equal(0, progress.Attempted);
        Assert.Equal("0/2", progress.ProgressText);
    }

    [Fact]
    public void RealSendsAfterADryRunStillCount()
    {
        var history = new List<StreakRunResult>
        {
            new()
            {
                RunTime = Today,
                FriendResults = new List<FriendMessageResult>
                {
                    new() { FriendId = "a", Username = "ana", Success = true },
                },
            },
            new()
            {
                RunTime = Today.AddHours(-2),
                DryRun = true,
                FriendResults = new List<FriendMessageResult>
                {
                    new() { FriendId = "b", Username = "budi", Success = true, DryRun = true },
                },
            },
        };

        var progress = DailyProgressCalculator.Calculate(history, enabledFriendCount: 2, today: Today);

        Assert.Equal(1, progress.SentToday);
        Assert.Equal("1/2", progress.ProgressText);
    }

    [Fact]
    public void RunHistoryRateIgnoresDryRunAttempts()
    {
        var history = new List<StreakRunResult>
        {
            new()
            {
                RunTime = Today,
                FriendResults = new List<FriendMessageResult>
                {
                    new() { FriendId = "a", Username = "ana", Success = true },
                    new() { FriendId = "b", Username = "budi", Success = false, DryRun = true },
                    new() { FriendId = "c", Username = "citra", Success = false, DryRun = true },
                },
            },
        };

        var stats = RunHistoryStats.ForLastDays(history, 7, Today);

        // Only the real attempt counts, so the rate is 100% rather than 33%.
        Assert.Equal(1, stats.Messages);
        Assert.Equal(1, stats.SuccessfulMessages);
        Assert.Equal(100, stats.SuccessPercent);
    }

    [Fact]
    public void ARehearsalStillLeavesFriendsAtRisk()
    {
        var history = new List<StreakRunResult>
        {
            new()
            {
                RunTime = Today.AddHours(9),
                DryRun = true,
                FriendResults = new List<FriendMessageResult>
                {
                    new() { FriendId = "a", Username = "ana", Success = true, DryRun = true },
                },
            },
        };

        var risk = StreakRiskEvaluator.Evaluate(history, enabledFriendCount: 1, now: LateEvening);

        Assert.True(risk.IsAtRisk);
        Assert.Equal(1, risk.PendingCount);
    }

    [Fact]
    public void LegacyHistoryWithoutTheDryRunFieldIsUnaffected()
    {
        // Older entries deserialize with DryRun = false, so they behave exactly as before.
        var history = new List<StreakRunResult>
        {
            new()
            {
                RunTime = Today,
                FriendResults = new List<FriendMessageResult>
                {
                    new() { FriendId = "a", Username = "ana", Success = true },
                },
            },
        };

        var progress = DailyProgressCalculator.Calculate(history, enabledFriendCount: 1, today: Today);
        var stats = RunHistoryStats.ForLastDays(history, 7, Today);

        Assert.Equal(1, progress.SentToday);
        Assert.Equal(1, stats.Messages);
    }
}
