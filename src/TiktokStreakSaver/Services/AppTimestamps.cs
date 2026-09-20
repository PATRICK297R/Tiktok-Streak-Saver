namespace TiktokStreakSaver.Services;

/// <summary>
/// Converts between the timestamps the app works with and the ones it writes down.
/// <para>
/// One rule, in one place: <strong>the app works in local wall-clock time, the file holds instants.</strong>
/// Every timestamp the app records is a local one - <c>DateTime.Now</c>, written the moment a message is
/// sent - and every question it asks about them is a local one: "was this today?", "how long ago was
/// that?". The file format is UTC, because an instant is unambiguous.
/// </para>
/// <para>
/// That translation has to happen when the value crosses the file boundary. It used to happen only on the
/// way out, so a timestamp came back as a UTC instant and every local question about it was answered with
/// the wrong day: a run at 06:00 in UTC+7 is 23:00 the previous day in UTC, so a morning run was filed
/// under yesterday. The dashboard showed no progress for a day that had run, and the "already messaged
/// today" check did not recognise its own messages, so it sent them again.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class AppTimestamps
{
    /// <summary>
    /// The value to write down: the same instant, as UTC.
    /// <para>
    /// A timestamp with no zone on it is read as local, which is what it has always meant here. It is how
    /// entries written by older builds arrive, and treating them as UTC would shift them by the device's
    /// offset every time the file was rewritten.
    /// </para>
    /// </summary>
    public static DateTime ToStored(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
    };

    /// <summary>
    /// The value to work with: the same instant, as local time.
    /// <para>
    /// A value with no zone on it is passed through untouched rather than converted. It is already a local
    /// wall-clock time - converting it would treat it as UTC and move it by the device's offset, which is
    /// how older entries would silently change the day they belong to.
    /// </para>
    /// </summary>
    public static DateTime FromStored(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value.ToLocalTime(),
        _ => value
    };
}
