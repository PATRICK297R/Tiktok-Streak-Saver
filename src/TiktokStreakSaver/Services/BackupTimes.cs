namespace TiktokStreakSaver.Services;

/// <summary>
/// Converts the daily run times between the app's own <see cref="TimeSpan"/> form and the "HH:mm"
/// text a backup file holds.
/// <para>
/// The text form is deliberate: a backup is something a user may open and read, and "21:00" says
/// more than a tick count. It is also the same shape the app already stores in preferences, so the
/// two cannot drift apart.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class BackupTimes
{
    /// <summary>
    /// Renders times as "HH:mm", sorted and de-duplicated, so a backup file is stable from one
    /// export to the next.
    /// </summary>
    public static List<string> Format(IEnumerable<TimeSpan>? times)
    {
        if (times is null)
            return new List<string>();

        return ScheduleCalculator.NormalizeDailyTimes(times)
            .Select(t => $"{t.Hours:D2}:{t.Minutes:D2}")
            .ToList();
    }

    /// <summary>
    /// Reads "HH:mm" entries back. Anything unreadable or out of range is dropped rather than
    /// guessed at, because a wrong hour would send messages at the wrong time; the caller
    /// substitutes the app default when nothing survives.
    /// </summary>
    public static List<TimeSpan> Parse(IEnumerable<string>? raw)
    {
        var parsed = new List<TimeSpan>();

        foreach (var entry in raw ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            var bits = entry.Trim().Split(':');
            if (bits.Length != 2)
                continue;

            if (!int.TryParse(bits[0], out var hour) || !int.TryParse(bits[1], out var minute))
                continue;

            if (hour is < 0 or > 23 || minute is < 0 or > 59)
                continue;

            parsed.Add(new TimeSpan(hour, minute, 0));
        }

        return ScheduleCalculator.NormalizeDailyTimes(parsed).ToList();
    }
}
