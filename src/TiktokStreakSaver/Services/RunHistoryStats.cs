namespace TiktokStreakSaver.Services;

using TiktokStreakSaver.Models;

/// <summary>
/// Success figures for a window of recent days.
/// </summary>
/// <param name="Days">Size of the window that was asked for.</param>
/// <param name="Runs">Runs recorded inside the window.</param>
/// <param name="Messages">Per-friend send attempts inside the window.</param>
/// <param name="SuccessfulMessages">Attempts that succeeded inside the window.</param>
/// <param name="DaysWithRuns">Days in the window that saw at least one run.</param>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public readonly record struct RunWindowStats(
    int Days,
    int Runs,
    int Messages,
    int SuccessfulMessages,
    int DaysWithRuns)
{
    /// <summary>0-100, or 0 when nothing was attempted in the window.</summary>
    public int SuccessPercent => Messages > 0
        ? (int)Math.Round(100.0 * SuccessfulMessages / Messages)
        : 0;

    public bool HasData => Messages > 0 || Runs > 0;
}

/// <summary>
/// Aggregates run history over a rolling window of days.
/// <para>
/// The History page previously only reported lifetime totals, which meant a good week and a bad week
/// looked identical once the history filled up. The window gives a recent, actionable figure.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class RunHistoryStats
{
    /// <summary>
    /// Totals for the last <paramref name="days"/> calendar days, ending on <paramref name="today"/>
    /// inclusive. So <c>days: 7</c> covers today plus the six days before it.
    /// </summary>
    public static RunWindowStats ForLastDays(IEnumerable<StreakRunResult>? history, int days, DateTime today)
    {
        var windowDays = Math.Max(1, days);
        var firstDay = today.Date.AddDays(-(windowDays - 1));

        int runs = 0;
        int messages = 0;
        int successful = 0;
        var daysWithRuns = new HashSet<DateTime>();

        if (history != null)
        {
            foreach (var run in history)
            {
                if (run is null)
                    continue;

                var runDay = run.RunTime.Date;
                if (runDay < firstDay || runDay > today.Date)
                    continue;

                runs++;
                daysWithRuns.Add(runDay);

                if (run.FriendResults is null)
                    continue;

                foreach (var friendResult in run.FriendResults)
                {
                    if (friendResult is null)
                        continue;

                    // A rehearsal sends nothing, so it must not inflate the success rate.
                    if (friendResult.DryRun)
                        continue;

                    messages++;

                    if (friendResult.Success)
                        successful++;
                }
            }
        }

        return new RunWindowStats(windowDays, runs, messages, successful, daysWithRuns.Count);
    }
}
