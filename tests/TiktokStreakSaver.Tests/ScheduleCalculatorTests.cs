using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class ScheduleCalculatorTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 10, 30, 0);

    // ── Fixed daily schedule ───────────────────────────────────────────────

    [Fact]
    public void FixedDailyStaysOnTodayWhenTheTimeIsStillAhead()
    {
        var next = ScheduleCalculator.NextFixedDaily(hour: 18, minute: 0, now: Now);

        Assert.Equal(new DateTime(2026, 9, 17, 18, 0, 0), next);
    }

    [Fact]
    public void FixedDailyRollsToTomorrowOnceTheTimeHasPassed()
    {
        var next = ScheduleCalculator.NextFixedDaily(hour: 8, minute: 0, now: Now);

        Assert.Equal(new DateTime(2026, 9, 18, 8, 0, 0), next);
    }

    [Fact]
    public void FixedDailyTreatsAnExactlyDueMomentAsPassed()
    {
        // Pins existing behaviour from both previous implementations so the boundary is a deliberate
        // decision rather than an accident of using ">" instead of ">=".
        var next = ScheduleCalculator.NextFixedDaily(hour: 10, minute: 30, now: Now);

        Assert.Equal(new DateTime(2026, 9, 18, 10, 30, 0), next);
    }

    [Fact]
    public void FixedDailyClampsOutOfRangeInput()
    {
        Assert.Equal(new DateTime(2026, 9, 17, 23, 59, 0), ScheduleCalculator.NextFixedDaily(99, 99, Now));
        Assert.Equal(new DateTime(2026, 9, 18, 0, 0, 0), ScheduleCalculator.NextFixedDaily(-5, -5, Now));
    }

    // ── Rolling interval ───────────────────────────────────────────────────

    [Fact]
    public void IntervalStartsFromNowWhenNothingHasRunYet()
    {
        var next = ScheduleCalculator.NextInterval(intervalHours: 6, lastRun: null, now: Now);

        Assert.Equal(Now.AddHours(6), next);
    }

    [Fact]
    public void IntervalCountsFromTheLastRunWhileThatMomentIsStillAhead()
    {
        var next = ScheduleCalculator.NextInterval(intervalHours: 6, lastRun: Now.AddHours(-2), now: Now);

        Assert.Equal(Now.AddHours(4), next);
    }

    [Fact]
    public void OverdueIntervalIsScheduledShortlyAheadInsteadOfInThePast()
    {
        // Regression guard. Only the scheduler used to apply this grace period, so the Dashboard's
        // "Next Scheduled Run" could render a timestamp that had already elapsed.
        var next = ScheduleCalculator.NextInterval(intervalHours: 6, lastRun: Now.AddHours(-20), now: Now);

        Assert.True(next > Now, $"expected a future run time, got {next}");
        Assert.Equal(Now.AddMinutes(ScheduleCalculator.OverdueGraceMinutes), next);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(999, 23)]
    [InlineData(24, 23)]
    public void IntervalClampsOutOfRangeHours(int configured, int expectedHours)
    {
        var next = ScheduleCalculator.NextInterval(configured, lastRun: null, now: Now);

        Assert.Equal(Now.AddHours(expectedHours), next);
    }

    // ── Mode selection ─────────────────────────────────────────────────────

    [Fact]
    public void DailyModeIgnoresIntervalSettings()
    {
        var next = ScheduleCalculator.Next(
            useFixedDaily: true,
            fixedHour: 20,
            fixedMinute: 15,
            intervalHours: 1,
            lastRun: null,
            now: Now);

        Assert.Equal(new DateTime(2026, 9, 17, 20, 15, 0), next);
    }

    [Fact]
    public void IntervalModeIgnoresFixedClockSettings()
    {
        var next = ScheduleCalculator.Next(
            useFixedDaily: false,
            fixedHour: 20,
            fixedMinute: 15,
            intervalHours: 3,
            lastRun: null,
            now: Now);

        Assert.Equal(Now.AddHours(3), next);
    }

    [Fact]
    public void NextAlwaysReturnsAFutureMoment()
    {
        // The alarm is set immediately after this is computed, so anything in the past would be
        // scheduled to fire instantly (or not at all).
        var lastRuns = new DateTime?[] { null, Now.AddMinutes(-1), Now.AddHours(-30), Now.AddDays(-5) };

        foreach (var lastRun in lastRuns)
        {
            foreach (var useFixedDaily in new[] { true, false })
            {
                var next = ScheduleCalculator.Next(useFixedDaily, 10, 30, 6, lastRun, Now);
                Assert.True(next > Now, $"mode fixed={useFixedDaily}, lastRun={lastRun}: got {next}");
            }
        }
    }

    // ── Multiple daily times ───────────────────────────────────────────────

    [Fact]
    public void DailyTimesPicksTheEarliestRemainingTimeToday()
    {
        var times = new[] { TimeSpan.FromHours(9), TimeSpan.FromHours(21) };

        var next = ScheduleCalculator.NextDailyTimes(times, Now);

        Assert.Equal(new DateTime(2026, 9, 17, 21, 0, 0), next);
    }

    [Fact]
    public void DailyTimesRollsToTomorrowsEarliestOnceEveryTimeHasPassed()
    {
        var times = new[] { TimeSpan.FromHours(6), TimeSpan.FromHours(8) };

        var next = ScheduleCalculator.NextDailyTimes(times, Now);

        Assert.Equal(new DateTime(2026, 9, 18, 6, 0, 0), next);
    }

    [Fact]
    public void DailyTimesSkipsAnExactlyDueTimeWhenALaterOneRemains()
    {
        // Better than the single-time behaviour: the day is not abandoned just because one entry
        // happens to be due at this very instant.
        var times = new[] { new TimeSpan(10, 30, 0), new TimeSpan(21, 0, 0) };

        var next = ScheduleCalculator.NextDailyTimes(times, Now);

        Assert.Equal(new DateTime(2026, 9, 17, 21, 0, 0), next);
    }

    [Fact]
    public void DailyTimesWithOnlyAnExactlyDueTimeRollsToTomorrow()
    {
        var times = new[] { new TimeSpan(10, 30, 0) };

        var next = ScheduleCalculator.NextDailyTimes(times, Now);

        Assert.Equal(new DateTime(2026, 9, 18, 10, 30, 0), next);
    }

    [Fact]
    public void DailyTimesFallsBackToMidnightWhenNothingIsConfigured()
    {
        var next = ScheduleCalculator.NextDailyTimes(Array.Empty<TimeSpan>(), Now);

        Assert.Equal(new DateTime(2026, 9, 18, 0, 0, 0), next);
        Assert.Equal(next, ScheduleCalculator.NextDailyTimes(null, Now));
    }

    [Fact]
    public void NormalizeDailyTimesSortsDeduplicatesAndDropsSeconds()
    {
        var normalized = ScheduleCalculator.NormalizeDailyTimes(new[]
        {
            TimeSpan.FromHours(21),
            new TimeSpan(9, 15, 45),      // seconds dropped
            TimeSpan.FromHours(9),        // sorts before 09:15
            new TimeSpan(21, 0, 30),      // de-duplicates with 21:00 once seconds are dropped
        });

        Assert.Equal(new[] { new TimeSpan(9, 0, 0), new TimeSpan(9, 15, 0), new TimeSpan(21, 0, 0) }, normalized);
    }

    [Fact]
    public void NormalizeDailyTimesClampsOutOfRangeValues()
    {
        var normalized = ScheduleCalculator.NormalizeDailyTimes(new[]
        {
            TimeSpan.FromHours(-4),
            TimeSpan.FromDays(2),
        });

        Assert.Equal(new[] { TimeSpan.Zero, new TimeSpan(23, 59, 0) }, normalized);
    }

    [Fact]
    public void DailyTimesModeIgnoresIntervalSettings()
    {
        var next = ScheduleCalculator.Next(
            useFixedDaily: true,
            fixedTimes: new[] { TimeSpan.FromHours(22) },
            intervalHours: 1,
            lastRun: null,
            now: Now);

        Assert.Equal(new DateTime(2026, 9, 17, 22, 0, 0), next);
    }

    // ── Adding another time ────────────────────────────────────────────────

    [Fact]
    public void NextFreeHourReturnsTheStartHourWhenNothingIsScheduled()
    {
        var next = ScheduleCalculator.NextFreeHour(Array.Empty<TimeSpan>(), startHour: 9);

        Assert.Equal(TimeSpan.FromHours(9), next);
    }

    [Fact]
    public void NextFreeHourSkipsHoursAlreadyInUse()
    {
        // Regression guard for the "Add another time" button doing nothing: it used to read the time
        // from the picker that edits the first entry, so the candidate was always already present.
        var times = new[] { TimeSpan.FromHours(9), TimeSpan.FromHours(10), TimeSpan.FromHours(11) };

        var next = ScheduleCalculator.NextFreeHour(times, startHour: 9);

        Assert.Equal(TimeSpan.FromHours(12), next);
    }

    [Fact]
    public void NextFreeHourWrapsAroundMidnight()
    {
        var times = new[] { TimeSpan.FromHours(23), TimeSpan.FromHours(0), TimeSpan.FromHours(1) };

        var next = ScheduleCalculator.NextFreeHour(times, startHour: 23);

        Assert.Equal(TimeSpan.FromHours(2), next);
    }

    [Fact]
    public void NextFreeHourIgnoresSecondsSoAQuarterPastDoesNotBlockTheHour()
    {
        var times = new[] { new TimeSpan(9, 15, 30) };

        var next = ScheduleCalculator.NextFreeHour(times, startHour: 9);

        Assert.Equal(TimeSpan.FromHours(9), next);
    }

    [Fact]
    public void NextFreeHourReturnsNullWhenEveryHourIsTaken()
    {
        var everyHour = Enumerable.Range(0, 24).Select(TimeSpan.FromHours).ToArray();

        Assert.Null(ScheduleCalculator.NextFreeHour(everyHour, startHour: 0));
    }

    [Fact]
    public void NextFreeHourFindsTheLastRemainingHour()
    {
        var allButOne = Enumerable.Range(0, 24)
            .Where(h => h != 7)
            .Select(TimeSpan.FromHours)
            .ToArray();

        Assert.Equal(TimeSpan.FromHours(7), ScheduleCalculator.NextFreeHour(allButOne, startHour: 0));
    }

    [Theory]
    [InlineData(-3, 21)]
    [InlineData(26, 2)]
    public void NextFreeHourAcceptsAnOutOfRangeStartHour(int startHour, int expectedHour)
    {
        // Wrapping keeps the callers simple: they can pass primary + 1 without bounds checks.
        Assert.Equal(TimeSpan.FromHours(expectedHour), ScheduleCalculator.NextFreeHour(Array.Empty<TimeSpan>(), startHour));
    }

    [Fact]
    public void NextFreeHourHandlesNull()
    {
        Assert.Equal(TimeSpan.FromHours(5), ScheduleCalculator.NextFreeHour(null, startHour: 5));
    }

    [Fact]
    public void AddingTimesOneByOneAlwaysProgressesUntilTheDayIsFull()
    {
        // Simulates pressing "Add another time" repeatedly: every press must add a distinct entry and
        // the sequence must terminate rather than spinning on a duplicate.
        var times = new List<TimeSpan> { TimeSpan.FromHours(9) };
        var added = new List<TimeSpan>();

        for (var i = 0; i < 23; i++)
        {
            var next = ScheduleCalculator.NextFreeHour(times, times[0].Hours + 1);
            Assert.NotNull(next);

            Assert.DoesNotContain(next!.Value, times);
            times.Add(next.Value);
            added.Add(next.Value);
        }

        Assert.Equal(23, added.Count);
        Assert.Equal(24, times.Count);
        Assert.Null(ScheduleCalculator.NextFreeHour(times, 0));
    }

    [Fact]
    public void DailyTimesModeAlwaysReturnsAFutureMoment()
    {
        var timeSets = new[]
        {
            new[] { TimeSpan.FromHours(0) },
            new[] { TimeSpan.FromHours(10) },
            new[] { TimeSpan.FromHours(23), TimeSpan.FromHours(23) },
            new[] { new TimeSpan(10, 30, 0) },
        };

        foreach (var times in timeSets)
        {
            var next = ScheduleCalculator.NextDailyTimes(times, Now);
            Assert.True(next > Now, $"times={string.Join(",", times)}: got {next}");
        }
    }
}
