using TiktokStreakSaver.Models;

namespace TiktokStreakSaver.Services;

/// <summary>
/// Outcome of a streak-risk check.
/// </summary>
/// <param name="IsAtRisk">True when friends are still unsent after the warning time.</param>
/// <param name="PendingCount">Enabled friends not messaged successfully today.</param>
/// <param name="TotalCount">Enabled friends in total.</param>
/// <param name="HoursLeft">Whole hours until the day rolls over.</param>
/// <param name="Summary">One-line explanation suitable for a notification body.</param>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public readonly record struct StreakRisk(
    bool IsAtRisk,
    int PendingCount,
    int TotalCount,
    int HoursLeft,
    string Summary);

/// <summary>
/// Decides whether the user should be warned that today's streaks are in danger.
/// <para>
/// The app only reported <em>after</em> a run finished, so a day where the run never fired - or fired
/// and failed - could quietly pass with the streaks breaking. This adds the missing early warning:
/// once the configured hour passes with friends still unsent, the user is told while there is still
/// time to act.
/// </para>
/// <para>
/// Uses <see cref="DailyProgressCalculator"/> so "already sent today" means the same thing here as it
/// does on the Dashboard, including friends skipped by the daily cooldown.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class StreakRiskEvaluator
{
    /// <summary>
    /// Local hour after which an unsent streak counts as at risk. 21:00 leaves a few hours to react
    /// while the user is still likely awake.
    /// </summary>
    public const int DefaultWarningHour = 21;

    public static StreakRisk Evaluate(
        IEnumerable<StreakRunResult>? history,
        int enabledFriendCount,
        DateTime now,
        int warningHour = DefaultWarningHour)
    {
        var total = Math.Max(0, enabledFriendCount);

        // Nothing to protect: no friends enabled means no streak can break.
        if (total == 0)
            return new StreakRisk(false, 0, 0, HoursUntilTomorrow(now), string.Empty);

        var progress = DailyProgressCalculator.Calculate(history, total, now.Date);
        var pending = Math.Max(0, total - progress.SentToday);

        if (pending == 0)
            return new StreakRisk(false, 0, total, HoursUntilTomorrow(now), string.Empty);

        var hour = Math.Clamp(warningHour, 0, 23);
        if (now.Hour < hour)
            return new StreakRisk(false, pending, total, HoursUntilTomorrow(now), string.Empty);

        var hoursLeft = HoursUntilTomorrow(now);
        return new StreakRisk(true, pending, total, hoursLeft, BuildSummary(pending, total, hoursLeft));
    }

    /// <summary>Whole hours until the next midnight; never negative.</summary>
    public static int HoursUntilTomorrow(DateTime now)
    {
        var remaining = now.Date.AddDays(1) - now;
        return Math.Max(0, (int)Math.Ceiling(remaining.TotalHours));
    }

    private static string BuildSummary(int pending, int total, int hoursLeft)
    {
        var who = pending == total
            ? $"All {total} friends"
            : $"{pending} of {total} friends";

        if (hoursLeft <= 0)
            return $"{who} still need a message today. The day is almost over.";

        var time = hoursLeft == 1 ? "1 hour" : $"{hoursLeft} hours";
        return $"{who} still need a message today - about {time} left.";
    }
}
