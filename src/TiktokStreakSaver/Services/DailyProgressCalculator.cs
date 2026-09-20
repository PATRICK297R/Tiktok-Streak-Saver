namespace TiktokStreakSaver.Services;

using TiktokStreakSaver.Models;

/// <summary>
/// Aggregated progress for a single day, as shown in the Dashboard overview card.
/// </summary>
/// <param name="RanToday">True when at least one run was recorded today.</param>
/// <param name="SentToday">Distinct friends that were messaged successfully today.</param>
/// <param name="Attempted">Distinct friends that were attempted today.</param>
/// <param name="SuccessPercent">0-100, or 0 when nothing was attempted.</param>
/// <param name="Expected">Denominator for the progress bar. Never below <see cref="SentToday"/>.</param>
/// <param name="Remaining">Friends still expected to be messaged today.</param>
/// <param name="Fraction">0-1 value for the progress drawable.</param>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public readonly record struct DailyProgress(
    bool RanToday,
    int SentToday,
    int Attempted,
    int SuccessPercent,
    int Expected,
    int Remaining,
    float Fraction)
{
    /// <summary>Label rendered inside the overview badge, e.g. "3/5".</summary>
    public string ProgressText => $"{SentToday}/{Expected}";
}

/// <summary>
/// Works out "today's" streak progress from run history.
/// <para>
/// Progress has to be summed across <em>every</em> run of the day rather than read off the newest
/// entry. A friend already messaged today is skipped by later runs, and a run where every friend was
/// skipped is still recorded - carrying no per-friend results at all. Reading only the newest run
/// therefore reported 0 sent immediately after such a run, making a day that did succeed look like it
/// had never run.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class DailyProgressCalculator
{
    public static DailyProgress Calculate(
        IEnumerable<StreakRunResult>? history,
        int enabledFriendCount,
        DateTime today)
    {
        var sentIds = new HashSet<string>(StringComparer.Ordinal);
        var attemptedIds = new HashSet<string>(StringComparer.Ordinal);
        var ranToday = false;

        if (history != null)
        {
            foreach (var run in history)
            {
                if (run is null || run.RunTime.Date != today.Date)
                    continue;

                ranToday = true;

                if (run.FriendResults is null)
                    continue;

                foreach (var friendResult in run.FriendResults)
                {
                    // A dry run never sends anything, so counting it would let a rehearsal look like a
                    // finished day and suppress the at-risk warning.
                    if (friendResult.DryRun)
                        continue;

                    var key = FriendKey(friendResult);
                    if (key is null)
                        continue;

                    attemptedIds.Add(key);

                    if (friendResult.Success)
                        sentIds.Add(key);
                }
            }
        }

        int sent = sentIds.Count;
        int attempted = attemptedIds.Count;

        int successPercent = attempted > 0
            ? (int)Math.Round(100.0 * sent / attempted)
            : 0;

        // A friend messaged today can be disabled or removed afterwards, so the denominator must
        // never fall below what was actually sent.
        int expected = Math.Max(enabledFriendCount, sent);
        int remaining = Math.Max(0, expected - sent);
        float fraction = expected > 0 ? (float)sent / expected : 0f;

        return new DailyProgress(ranToday, sent, attempted, successPercent, expected, remaining, fraction);
    }

    /// <summary>
    /// Stable identity for de-duplication. <see cref="FriendMessageResult.FriendId"/> is preferred;
    /// the username is the fallback for entries written before an id was recorded.
    /// </summary>
    private static string? FriendKey(FriendMessageResult? friendResult)
    {
        if (friendResult is null)
            return null;

        if (!string.IsNullOrEmpty(friendResult.FriendId))
            return friendResult.FriendId;

        return string.IsNullOrEmpty(friendResult.Username) ? null : friendResult.Username;
    }
}
