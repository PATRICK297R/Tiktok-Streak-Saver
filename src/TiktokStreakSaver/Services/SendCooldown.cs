namespace TiktokStreakSaver.Services;

/// <summary>
/// Decides whether a friend has already been messaged for the schedule slot that is currently running.
/// <para>
/// This replaces a check that compared only the calendar day: <c>LastMessageSent.Date == today</c>. That
/// made a multi-time schedule useless, because the first run of the day claimed every friend and the
/// 15:00 and 21:00 runs then skipped all of them - and reported the result as a success.
/// </para>
/// <para>
/// The unit is the slot, not the day. A slot opens at the scheduled time and stays open until the next
/// one, so a friend messaged at 08:00 is due again at 15:00, and a friend messaged at 15:05 is not.
/// </para>
/// <para>
/// Two schedules have to be covered, and they mean different things by "slot":
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Fixed daily times.</b> The slot opens at the most recent configured time at or before now. With
///     no times configured the schedule behaves as a single midnight run, matching
///     <see cref="ScheduleCalculator.NextDailyTimes"/>.
///   </description></item>
///   <item><description>
///     <b>Rolling interval.</b> There are no clock times to read, so the slot opens when the previous run
///     started. That is the same event the interval is measured from, which keeps the two in step: the
///     next run's slot opens after the previous run, so the friends it messaged become due again.
///   </description></item>
/// </list>
/// <para>
/// Kept free of MAUI and Android dependencies so the arithmetic can be tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class SendCooldown
{
    /// <summary>
    /// The shortest gap allowed between two sends to the same friend, whatever the schedule says.
    /// <para>
    /// The slot rule alone would permit two slots a minute apart if the user configured them that way, and
    /// a burst like that is indistinguishable from a bug to TikTok. Thirty minutes is short enough to stay
    /// out of the way of any sensible schedule - the smallest gap the settings offer is an hour - and long
    /// enough to absorb a double-triggered alarm.
    /// </para>
    /// </summary>
    public const int MinimumGapMinutes = 30;

    /// <summary>
    /// When the currently open slot started.
    /// </summary>
    /// <param name="useFixedDaily">True for "same times every day", false for "every N hours".</param>
    /// <param name="dailyTimes">The configured times of day. Empty means a single midnight run.</param>
    /// <param name="lastRunStartedAt">
    /// When the previous run began. Only used for the interval schedule, which has no clock times.
    /// </param>
    /// <param name="now">Current local time.</param>
    public static DateTime SlotStart(
        bool useFixedDaily,
        IReadOnlyList<TimeSpan>? dailyTimes,
        DateTime? lastRunStartedAt,
        DateTime now)
    {
        if (!useFixedDaily)
        {
            // No clock times to read, so the slot opens with the run the interval is measured from. With
            // no previous run there is no slot to be inside of, so the day start is used and every friend
            // is due - which is what a first run should do.
            return lastRunStartedAt ?? now.Date;
        }

        var times = ScheduleCalculator.NormalizeDailyTimes(dailyTimes);

        if (times.Count == 0)
            return now.Date;

        // The latest time that has already arrived today. Every earlier one belongs to a closed slot.
        var start = now.Date;
        var found = false;

        foreach (var time in times)
        {
            var candidate = now.Date.Add(time);
            if (candidate <= now)
            {
                start = candidate;
                found = true;
            }
        }

        // Before the first time of the day: the open slot is the last one from yesterday, which is what
        // keeps a run that fires just after midnight from treating the whole day as untouched.
        if (!found)
            start = now.Date.AddDays(-1).Add(times[^1]);

        return start;
    }

    /// <summary>
    /// True when this friend has already been messaged in the open slot, and should be left alone.
    /// <para>
    /// The minimum gap is applied as well as the slot, so a schedule with two times close together cannot
    /// produce two sends back to back.
    /// </para>
    /// </summary>
    public static bool AlreadySentForSlot(DateTime? lastSentAt, DateTime slotStart, DateTime now)
    {
        if (lastSentAt is not { } sentAt)
            return false;

        // The gap guard comes first: it holds regardless of which slot a send landed in.
        if (now - sentAt < TimeSpan.FromMinutes(MinimumGapMinutes))
            return true;

        return sentAt >= slotStart;
    }

    /// <summary>
    /// The slot rule applied to a whole friend list at once, for the run's skip log.
    /// </summary>
    public static bool ShouldSkip(DateTime? lastSentAt, DateTime slotStart, DateTime now) =>
        AlreadySentForSlot(lastSentAt, slotStart, now);
}
