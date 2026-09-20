using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// The Dashboard and the Activity page both print these phrases from one formatter, so these tests
/// pin the exact wording both surfaces rely on.
/// </summary>
public class RunRecencyTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0);

    [Fact]
    public void NoRunYetReadsNever()
    {
        Assert.Equal("Never", RunRecency.DescribeLastRun(null, Now));
    }

    [Fact]
    public void ARunSecondsAgoReadsJustNow()
    {
        Assert.Equal("Just now", RunRecency.DescribeLastRun(Now.AddSeconds(-30), Now));
    }

    [Fact]
    public void ATimestampInTheFutureDoesNotProduceANegativePhrase()
    {
        // A clock that jumped backwards must not print "-5 minutes ago".
        Assert.Equal("Just now", RunRecency.DescribeLastRun(Now.AddMinutes(5), Now));
    }

    [Theory]
    [InlineData(1, "1 minute ago")]
    [InlineData(2, "2 minutes ago")]
    [InlineData(59, "59 minutes ago")]
    public void MinutesAreReportedUpToAnHour(int minutes, string expected)
    {
        Assert.Equal(expected, RunRecency.DescribeLastRun(Now.AddMinutes(-minutes), Now));
    }

    [Theory]
    [InlineData(1, "1 hour ago")]
    [InlineData(2, "2 hours ago")]
    [InlineData(23, "23 hours ago")]
    public void HoursAreReportedUpToADay(int hours, string expected)
    {
        Assert.Equal(expected, RunRecency.DescribeLastRun(Now.AddHours(-hours), Now));
    }

    [Fact]
    public void ExactlyAnHourIsAnHourNotSixtyMinutes()
    {
        Assert.Equal("1 hour ago", RunRecency.DescribeLastRun(Now.AddMinutes(-60), Now));
    }

    [Fact]
    public void BeyondADayFallsBackToACalendarStamp()
    {
        var lastRun = Now.AddHours(-24);

        // Compared against the same formatting call so the test does not depend on the runner's
        // culture.
        Assert.Equal(lastRun.ToString("MMM dd, HH:mm"), RunRecency.DescribeLastRun(lastRun, Now));
    }

    [Fact]
    public void AnOlderRunIsNotMistakenForSomethingRecent()
    {
        var lastRun = Now.AddDays(-30);
        var described = RunRecency.DescribeLastRun(lastRun, Now);

        Assert.DoesNotContain("ago", described);
        Assert.Equal(lastRun.ToString("MMM dd, HH:mm"), described);
    }

    [Fact]
    public void AnUnknownDurationIsDroppedRatherThanPlaceholdered()
    {
        Assert.Null(RunRecency.DescribeDuration(null));
        Assert.Null(RunRecency.DescribeDuration(TimeSpan.Zero));
        Assert.Null(RunRecency.DescribeDuration(TimeSpan.FromSeconds(-5)));
    }

    [Theory]
    [InlineData(0.4, "< 1s")]
    [InlineData(1, "1s")]
    [InlineData(45, "45s")]
    [InlineData(59, "59s")]
    public void ShortDurationsReadInSeconds(double seconds, string expected)
    {
        Assert.Equal(expected, RunRecency.DescribeDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(60, "1m 0s")]
    [InlineData(95, "1m 35s")]
    [InlineData(125, "2m 5s")]
    public void LongerDurationsReadInMinutesAndSeconds(double seconds, string expected)
    {
        Assert.Equal(expected, RunRecency.DescribeDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void TheActivityLineReportsWhenTheRunHappened()
    {
        var line = RunRecency.DescribeLastRunWithDuration(Now.AddMinutes(-5), TimeSpan.FromSeconds(12), Now);

        Assert.Equal("Last run: 5 minutes ago · 12s", line);
    }

    [Fact]
    public void TheActivityLineOmitsTheDurationWhenItWasNeverRecorded()
    {
        var line = RunRecency.DescribeLastRunWithDuration(Now.AddMinutes(-5), null, Now);

        Assert.Equal("Last run: 5 minutes ago", line);
        Assert.DoesNotContain("·", line);
    }

    /// <summary>
    /// The reported bug: a run that happened, whose duration was never captured, must not be reported
    /// as missing.
    /// </summary>
    [Fact]
    public void ARunWithNoRecordedDurationNeverReadsAsN_A()
    {
        var line = RunRecency.DescribeLastRunWithDuration(Now.AddHours(-3), null, Now);

        Assert.DoesNotContain("N/A", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Last run: 3 hours ago", line);
    }

    [Fact]
    public void NoRunYetReadsAsNeverWithNoDurationClause()
    {
        var line = RunRecency.DescribeLastRunWithDuration(null, null, Now);

        Assert.Equal("Last run: Never", line);
        Assert.DoesNotContain("N/A", line, StringComparison.OrdinalIgnoreCase);
    }
}
