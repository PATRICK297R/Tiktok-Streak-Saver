using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class StreakRiskEvaluatorTests
{
    private static readonly DateTime Today = new(2026, 9, 17, 0, 0, 0);

    // 22:00 - past the default 21:00 warning hour, 2 hours left in the day.
    private static readonly DateTime LateEvening = Today.AddHours(22);

    // 08:00 - still plenty of time.
    private static readonly DateTime Morning = Today.AddHours(8);

    private static StreakRunResult Run(DateTime when, params (string Id, bool Success)[] results) =>
        new()
        {
            RunTime = when,
            FriendResults = results
                .Select(r => new FriendMessageResult { FriendId = r.Id, Username = r.Id, Success = r.Success })
                .ToList(),
        };

    [Fact]
    public void NotAtRiskWhenThereAreNoEnabledFriends()
    {
        var risk = StreakRiskEvaluator.Evaluate(null, enabledFriendCount: 0, now: LateEvening);

        Assert.False(risk.IsAtRisk);
        Assert.Equal(0, risk.TotalCount);
        Assert.Empty(risk.Summary);
    }

    [Fact]
    public void NotAtRiskWhenEveryoneWasMessagedToday()
    {
        var history = new List<StreakRunResult>
        {
            Run(Today.AddHours(9), ("a", true), ("b", true)),
        };

        var risk = StreakRiskEvaluator.Evaluate(history, 2, LateEvening);

        Assert.False(risk.IsAtRisk);
        Assert.Equal(0, risk.PendingCount);
    }

    [Fact]
    public void NotAtRiskBeforeTheWarningHour()
    {
        // Nothing sent yet, but it is only 08:00 - no reason to nag.
        var risk = StreakRiskEvaluator.Evaluate(null, 3, Morning);

        Assert.False(risk.IsAtRisk);
        Assert.Equal(3, risk.PendingCount);
    }

    [Fact]
    public void AtRiskAfterTheWarningHourWhenFriendsRemainUnsent()
    {
        var risk = StreakRiskEvaluator.Evaluate(null, 3, LateEvening);

        Assert.True(risk.IsAtRisk);
        Assert.Equal(3, risk.PendingCount);
        Assert.Equal(3, risk.TotalCount);
        Assert.Equal(2, risk.HoursLeft);
        Assert.Equal("All 3 friends still need a message today - about 2 hours left.", risk.Summary);
    }

    [Fact]
    public void AtRiskAtExactlyTheWarningHour()
    {
        var risk = StreakRiskEvaluator.Evaluate(null, 1, Today.AddHours(StreakRiskEvaluator.DefaultWarningHour));

        Assert.True(risk.IsAtRisk);
    }

    [Fact]
    public void CountsOnlyStillPendingFriendsAfterAPartialRun()
    {
        var history = new List<StreakRunResult>
        {
            Run(Today.AddHours(9), ("a", true), ("b", true), ("c", false)),
        };

        var risk = StreakRiskEvaluator.Evaluate(history, 5, LateEvening);

        Assert.True(risk.IsAtRisk);
        Assert.Equal(3, risk.PendingCount);
        Assert.Equal(5, risk.TotalCount);
        Assert.Equal("3 of 5 friends still need a message today - about 2 hours left.", risk.Summary);
    }

    [Fact]
    public void IgnoresRunsFromOtherDays()
    {
        var history = new List<StreakRunResult>
        {
            Run(Today.AddDays(-1).AddHours(9), ("a", true)),
        };

        var risk = StreakRiskEvaluator.Evaluate(history, 1, LateEvening);

        Assert.True(risk.IsAtRisk);
        Assert.Equal(1, risk.PendingCount);
    }

    [Fact]
    public void FriendSkippedByCooldownStillCountsAsSent()
    {
        // The cooldown appears in history as an earlier run that succeeded, so the friend is not
        // reported as at risk later the same day.
        var history = new List<StreakRunResult>
        {
            Run(Today.AddHours(1), ("a", true)),
        };

        var risk = StreakRiskEvaluator.Evaluate(history, 1, LateEvening);

        Assert.False(risk.IsAtRisk);
    }

    [Fact]
    public void HonoursACustomWarningHour()
    {
        Assert.True(StreakRiskEvaluator.Evaluate(null, 1, Morning, warningHour: 7).IsAtRisk);
        Assert.False(StreakRiskEvaluator.Evaluate(null, 1, Morning, warningHour: 23).IsAtRisk);
    }

    [Fact]
    public void ClampsAnOutOfRangeWarningHour()
    {
        // -5 clamps to 0, so any time of day is already past it.
        Assert.True(StreakRiskEvaluator.Evaluate(null, 1, LateEvening, warningHour: -5).IsAtRisk);

        // 99 clamps to 23, so 22:00 is still before it.
        Assert.False(StreakRiskEvaluator.Evaluate(null, 1, LateEvening, warningHour: 99).IsAtRisk);
    }

    [Theory]
    [InlineData(0, 0, 24)]
    [InlineData(0, 30, 24)]
    [InlineData(12, 0, 12)]
    [InlineData(22, 0, 2)]
    [InlineData(23, 1, 1)]
    [InlineData(23, 59, 1)]
    public void HoursUntilTomorrowCountsWholeHoursAhead(int hour, int minute, int expected)
    {
        var now = Today.AddHours(hour).AddMinutes(minute);

        Assert.Equal(expected, StreakRiskEvaluator.HoursUntilTomorrow(now));
    }

    [Fact]
    public void SummaryMentionsTheDayBeingAlmostOverInTheFinalHour()
    {
        var risk = StreakRiskEvaluator.Evaluate(null, 2, Today.AddHours(23).AddMinutes(30));

        Assert.True(risk.IsAtRisk);
        Assert.Equal(1, risk.HoursLeft);
        Assert.Contains("1 hour left", risk.Summary);
    }

    [Fact]
    public void UsesTheSingularFormForASingleHour()
    {
        var risk = StreakRiskEvaluator.Evaluate(null, 2, Today.AddHours(23));

        Assert.Contains("1 hour left", risk.Summary);
        Assert.DoesNotContain("1 hours", risk.Summary);
    }
}
