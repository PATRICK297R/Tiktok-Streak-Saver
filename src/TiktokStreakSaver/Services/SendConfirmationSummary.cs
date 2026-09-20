using TiktokStreakSaver.Models;

namespace TiktokStreakSaver.Services;


/// <summary>
/// Decides whether a run may claim everything was delivered, and sums the confirmation state.
/// <para>
/// Kept separate from <see cref="SendVerification"/> because it works on the stored per-friend rows
/// rather than on the live signals.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class SendConfirmationSummary
{
    /// <summary>
    /// Counts the outcomes across a set of results, ignoring dry-run rows.
    /// </summary>
    public static (int Attempted, int Confirmed, int Unverified, int Failed) Summarize(
        IEnumerable<FriendMessageResult>? results)
    {
        int attempted = 0, confirmed = 0, unverified = 0, failed = 0;

        if (results is null)
            return (0, 0, 0, 0);

        foreach (var result in results)
        {
            if (result is null || result.DryRun)
                continue;

            attempted++;

            if (!result.Success)
            {
                failed++;
                continue;
            }

            if (result.Verified == true)
                confirmed++;
            else
                unverified++;
        }

        return (attempted, confirmed, unverified, failed);
    }

    /// <summary>
    /// True only when there was at least one attempt and every one of them was confirmed. An
    /// unverified send keeps this false, which is the point: the app should not claim delivery it
    /// cannot stand behind.
    /// </summary>
    public static bool AllDelivered(IEnumerable<FriendMessageResult>? results)
    {
        var (attempted, confirmed, _, _) = Summarize(results);

        return attempted > 0 && confirmed == attempted;
    }

    /// <summary>Short human summary, e.g. "3 sent, 1 unverified".</summary>
    public static string Describe(IEnumerable<FriendMessageResult>? results)
    {
        var (attempted, confirmed, unverified, failed) = Summarize(results);

        if (attempted == 0)
            return "nothing to send";

        var parts = new List<string> { $"{confirmed} sent" };

        if (unverified > 0)
            parts.Add($"{unverified} unverified");
        if (failed > 0)
            parts.Add($"{failed} failed");

        return string.Join(", ", parts);
    }
}
