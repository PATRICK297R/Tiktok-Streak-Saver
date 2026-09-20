namespace TiktokStreakSaver.Services;

/// <summary>
/// Turns a "last run" timestamp into the short phrase the Dashboard and the Activity page show.
/// <para>
/// The two pages used to work this out separately and drifted apart: the Activity page described the
/// last run purely from the run's recorded <see cref="TiktokStreakSaver.Models.StreakRunResult.Duration"/>,
/// which nothing ever wrote, so it printed "Last run: N/A" while the Dashboard - reading the run
/// timestamp instead - happily showed the real time. Both surfaces now go through this one formatter,
/// so that contradiction cannot come back.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class RunRecency
{
    /// <summary>
    /// How long ago the run happened, in the same words everywhere.
    /// </summary>
    public static string DescribeLastRun(DateTime? lastRun, DateTime now)
    {
        if (!lastRun.HasValue)
            return AppStrings.T("Dash_Never");

        var elapsed = now - lastRun.Value;

        // A clock that moved backwards, or a timestamp written moments into the future, must not
        // produce "-3 minutes ago".
        if (elapsed < TimeSpan.FromMinutes(1))
            return AppStrings.T("Rec_JustNow");

        if (elapsed.TotalMinutes < 60)
            return Ago((int)elapsed.TotalMinutes, "Rec_MinuteAgo", "Rec_MinutesAgo");

        if (elapsed.TotalHours < 24)
            return Ago((int)elapsed.TotalHours, "Rec_HourAgo", "Rec_HoursAgo");

        return lastRun.Value.ToString("MMM dd, HH:mm");
    }

    /// <summary>
    /// How long the run took, or <c>null</c> when that was never captured - an entry written by an
    /// older build, or a run that never got far enough to be timed.
    /// <para>
    /// Callers drop the clause entirely rather than printing a placeholder, so a missing duration can
    /// never be mistaken for a missing run.
    /// </para>
    /// </summary>
    public static string? DescribeDuration(TimeSpan? duration)
    {
        if (duration is not { } value || value <= TimeSpan.Zero)
            return null;

        if (value.TotalSeconds < 1)
            return AppStrings.T("Rec_DurationUnderSecond");

        if (value.TotalMinutes >= 1)
            return string.Format(AppStrings.T("Rec_DurationMinutesSeconds"), (int)value.TotalMinutes, value.Seconds);

        return string.Format(AppStrings.T("Rec_DurationSeconds"), (int)value.TotalSeconds);
    }

    /// <summary>
    /// The full Activity-page line: when the last run happened, plus how long it took when that is
    /// known.
    /// </summary>
    public static string DescribeLastRunWithDuration(DateTime? lastRun, TimeSpan? duration, DateTime now)
    {
        var sentence = string.Format(AppStrings.T("Hist_LastRun"), DescribeLastRun(lastRun, now));
        var durationText = DescribeDuration(duration);

        return durationText is null ? sentence : $"{sentence} · {durationText}";
    }

    private static string Ago(int amount, string singularKey, string pluralKey) =>
        string.Format(AppStrings.T(amount == 1 ? singularKey : pluralKey), amount);
}
