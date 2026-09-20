namespace TiktokStreakSaver.Services;

/// <summary>
/// Works out when the next streak run is due.
/// <para>
/// Both the alarm that actually fires (<c>StreakScheduler.ScheduleNextRun</c>) and the "Next Scheduled
/// Run" label on the Dashboard (via <c>SettingsService.GetNextRunTime</c>) go through here, so the two
/// can no longer disagree. Previously only the scheduler applied the overdue-run grace period, so the
/// Dashboard could display a next-run time that was already in the past.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly. The caller supplies
/// "now" instead of reading the clock, which keeps the calculations deterministic.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class ScheduleCalculator
{
    /// <summary>
    /// How far ahead to schedule when an interval run is already overdue. The alarm is only set
    /// moments after this is computed, so it needs a small guard band to stay in the future.
    /// </summary>
    public const int OverdueGraceMinutes = 1;

    public const int MinIntervalHours = 1;
    public const int MaxIntervalHours = 23;

    /// <summary>
    /// Standard "next run" for the configured schedule.
    /// </summary>
    /// <param name="useFixedDaily">True for "same time every day", false for "every N hours".</param>
    /// <param name="fixedHour">Hour of day, 0-23, used when <paramref name="useFixedDaily"/> is true.</param>
    /// <param name="fixedMinute">Minute, 0-59, used when <paramref name="useFixedDaily"/> is true.</param>
    /// <param name="intervalHours">Hours between runs, used when <paramref name="useFixedDaily"/> is false.</param>
    /// <param name="lastRun">Time of the previous run, or null when none has run yet.</param>
    /// <param name="now">Current local time.</param>
    public static DateTime Next(
        bool useFixedDaily,
        int fixedHour,
        int fixedMinute,
        int intervalHours,
        DateTime? lastRun,
        DateTime now)
        => useFixedDaily
            ? NextFixedDaily(fixedHour, fixedMinute, now)
            : NextInterval(intervalHours, lastRun, now);

    /// <summary>
    /// Standard "next run" for a rolling interval, used when daily scheduling is off.
    /// </summary>
    public static DateTime Next(int intervalHours, DateTime? lastRun, DateTime now)
        => NextInterval(intervalHours, lastRun, now);

    /// <summary>
    /// Standard "next run" for daily scheduling with one or more times of day.
    /// </summary>
    public static DateTime Next(
        bool useFixedDaily,
        IReadOnlyList<TimeSpan>? fixedTimes,
        int intervalHours,
        DateTime? lastRun,
        DateTime now)
        => useFixedDaily
            ? NextDailyTimes(fixedTimes, now)
            : NextInterval(intervalHours, lastRun, now);

    /// <summary>
    /// Earliest occurrence among several times of day.
    /// <para>
    /// Returns today's earliest time that is still ahead; when every time has passed, the earliest one
    /// rolls to tomorrow. With an empty list this behaves as a single midnight run, which keeps the
    /// return value total rather than nullable for callers that must schedule something.
    /// </para>
    /// </summary>
    public static DateTime NextDailyTimes(IReadOnlyList<TimeSpan>? times, DateTime now)
    {
        var candidates = NormalizeDailyTimes(times);

        if (candidates.Count == 0)
            return NextFixedDaily(0, 0, now);

        foreach (var time in candidates)
        {
            var candidate = now.Date.Add(time);
            if (candidate > now)
                return candidate;
        }

        return now.Date.AddDays(1).Add(candidates[0]);
    }

    /// <summary>
    /// First whole hour that is not already scheduled, searched from <paramref name="startHour"/> and
    /// wrapping around the clock. Returns null when all 24 hours are taken.
    /// <para>
    /// Exists so "add another time" always has something to add. Returning a time that is already in
    /// use would make the button look broken, which is exactly what happened when the added time was
    /// taken from the same picker that edits the first entry - the value it read was always already
    /// present, so nothing was ever added.
    /// </para>
    /// </summary>
    public static TimeSpan? NextFreeHour(IEnumerable<TimeSpan>? times, int startHour)
    {
        var taken = NormalizeDailyTimes(times).ToHashSet();
        var start = ((startHour % 24) + 24) % 24;

        for (var offset = 0; offset < 24; offset++)
        {
            var candidate = TimeSpan.FromHours((start + offset) % 24);

            if (!taken.Contains(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Clamps to a real time of day, drops sub-minute precision, de-duplicates and sorts. Sorting is
    /// what makes <see cref="NextDailyTimes"/> able to simply take the first still-ahead entry.
    /// </summary>
    public static IReadOnlyList<TimeSpan> NormalizeDailyTimes(IEnumerable<TimeSpan>? times)
    {
        if (times is null)
            return Array.Empty<TimeSpan>();

        var unique = new SortedSet<TimeSpan>();

        foreach (var time in times)
        {
            var clamped = time < TimeSpan.Zero
                ? TimeSpan.Zero
                : time >= TimeSpan.FromDays(1)
                    ? new TimeSpan(23, 59, 0)
                    : new TimeSpan(time.Hours, time.Minutes, 0);

            unique.Add(clamped);
        }

        return unique.ToList();
    }

    /// <summary>
    /// Today at <paramref name="hour"/>:<paramref name="minute"/>, or the same time tomorrow when that
    /// moment has already passed.
    /// </summary>
    public static DateTime NextFixedDaily(int hour, int minute, DateTime now)
    {
        var clampedHour = Math.Clamp(hour, 0, 23);
        var clampedMinute = Math.Clamp(minute, 0, 59);

        var candidate = now.Date.AddHours(clampedHour).AddMinutes(clampedMinute);

        // A moment that is exactly due counts as already passed and rolls to the next occurrence.
        // This preserves the behaviour both previous implementations had (">" here, "<= now" in the
        // scheduler); it is pinned by a test so the edge case is a decision rather than an accident.
        return candidate > now ? candidate : candidate.AddDays(1);
    }

    /// <summary>
    /// <paramref name="lastRun"/> plus <paramref name="intervalHours"/>. With no previous run the
    /// interval starts from now; when the computed slot is already in the past the run is overdue, so
    /// it is scheduled a short grace period ahead instead of in the past.
    /// </summary>
    public static DateTime NextInterval(int intervalHours, DateTime? lastRun, DateTime now)
    {
        var hours = Math.Clamp(intervalHours, MinIntervalHours, MaxIntervalHours);

        if (!lastRun.HasValue)
            return now.AddHours(hours);

        var candidate = lastRun.Value.AddHours(hours);

        return candidate < now ? now.AddMinutes(OverdueGraceMinutes) : candidate;
    }
}
