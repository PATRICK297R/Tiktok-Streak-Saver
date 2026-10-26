namespace TiktokStreakSaver.Services;

/// <summary>
/// What is known about a message that was attempted today.
/// </summary>
public enum PendingSendState
{
    /// <summary>
    /// The send was started and no result ever came back - the process was killed while it was in
    /// flight. The message may or may not have reached TikTok.
    /// </summary>
    Unknown,

    /// <summary>The send was confirmed by the automation. Messaging again would pile a second message on.</summary>
    Sent,

    /// <summary>The send definitely did not happen, so trying again is safe.</summary>
    Failed
}

/// <summary>
/// A durable record of the messages this app has attempted to send today.
/// <para>
/// This exists because the per-friend cooldown is not safe on its own. Two gaps made a run that the
/// system killed send the same message twice:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>The record was written after the fact.</b> <c>LastMessageSent</c> is only set once the
///     automation reports back, so a kill between "the message went out" and "the result arrived"
///     left no trace of it - and on a slow phone that window is seconds wide, once per friend.
///   </description></item>
///   <item><description>
///     <b>The record was not on disk yet.</b> Preferences writes go through Android's
///     <c>apply()</c>, which is asynchronous; a process killed outright can lose writes that were
///     already made in memory. The one record that stops a duplicate was the one most likely to be
///     lost.
///   </description></item>
/// </list>
/// <para>
/// So each attempt is written <em>before</em> the message goes out, and flushed to disk rather than
/// queued. An entry with no result is reported as <see cref="PendingSendState.Unknown"/>, which callers
/// treat as "leave it alone": sending a second message is the harm being prevented, and a duplicate
/// cannot be undone.
/// </para>
/// <para>
/// Append-only, one record per line, because a rewrite would have to read-modify-write the whole file
/// and a kill in the middle of that would lose every earlier record too.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested against a temp file.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public sealed class PendingSendStore
{
    private const char Separator = '|';
    private const string AttemptKind = "A";
    private const string SentKind = "S";
    private const string FailedKind = "F";

    private readonly object _writeLock = new();

    public PendingSendStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A record file path is required.", nameof(filePath));

        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>
    /// One "this send is starting" record.
    /// <para>
    /// The username is written last on purpose: a group's display name is free text and may contain the
    /// separator, so only the first three fields are split and the rest is taken whole.
    /// </para>
    /// </summary>
    public static string FormatAttempt(DateTime at, string friendId, string? username) =>
        $"{AttemptKind}{Separator}{at.Ticks}{Separator}{friendId}{Separator}{username ?? string.Empty}";

    /// <summary>One "the message went out" record.</summary>
    public static string FormatSent(DateTime at, string friendId) =>
        $"{SentKind}{Separator}{at.Ticks}{Separator}{friendId}";

    /// <summary>One "nothing was sent" record.</summary>
    public static string FormatFailed(DateTime at, string friendId) =>
        $"{FailedKind}{Separator}{at.Ticks}{Separator}{friendId}";

    /// <summary>
    /// Folds the records into one outcome per entry, counting only those at or after
    /// <paramref name="since"/>.
    /// <para>
    /// The cutoff is a moment rather than a day, because a schedule can run several times in one day. A
    /// "sent" record from the 08:00 run must not stop the 15:00 run from messaging the same friend, and a
    /// per-day fold made it do exactly that.
    /// </para>
    /// <para>
    /// The last record for an entry wins, so an attempt followed by a result reports the result, and a
    /// retry that finally succeeded reports success rather than the earlier attempt.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, PendingSendState> FoldSince(
        IEnumerable<string?>? lines, DateTime since)
    {
        var states = new Dictionary<string, PendingSendState>(StringComparer.Ordinal);

        foreach (var line in lines ?? [])
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split(Separator, 4);

            if (parts.Length < 3)
                continue;

            if (!long.TryParse(parts[1], out var ticks))
                continue;

            DateTime at;
            try
            {
                at = new DateTime(ticks);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            if (at < since)
                continue;

            var friendId = parts[2];
            if (string.IsNullOrEmpty(friendId))
                continue;

            states[friendId] = parts[0] switch
            {
                SentKind => PendingSendState.Sent,
                FailedKind => PendingSendState.Failed,
                AttemptKind => PendingSendState.Unknown,
                // An unknown kind keeps whatever was already known rather than guessing: a record this
                // build does not understand must not be read as "safe to send again".
                _ => states.TryGetValue(friendId, out var existing) ? existing : PendingSendState.Unknown
            };
        }

        return states;
    }

    /// <summary>
    /// Folds the records for one calendar day. Kept for callers that genuinely mean a day - the retry
    /// budget and the at-risk warning - while the run itself uses <see cref="FoldSince"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, PendingSendState> FoldToday(
        IEnumerable<string?>? lines, DateTime day)
        => FoldSince(
            (lines ?? []).Where(line =>
            {
                if (string.IsNullOrWhiteSpace(line))
                    return false;

                var parts = line.Split(Separator, 4);
                return parts.Length >= 3
                    && long.TryParse(parts[1], out var ticks)
                    && SafeDate(ticks)?.Date == day.Date;
            }),
            day.Date);

    private static DateTime? SafeDate(long ticks)
    {
        try
        {
            return new DateTime(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Writes an attempt record and flushes it to disk before returning.</summary>
    public void MarkAttempted(string friendId, string? username, DateTime at) =>
        AppendDurably(FormatAttempt(at, friendId, username));

    /// <summary>Writes a success record and flushes it to disk before returning.</summary>
    public void MarkSent(string friendId, DateTime at) =>
        AppendDurably(FormatSent(at, friendId));

    /// <summary>Writes a definite-failure record and flushes it to disk before returning.</summary>
    public void MarkFailed(string friendId, DateTime at) =>
        AppendDurably(FormatFailed(at, friendId));

    /// <summary>Every entry with a record at or after <paramref name="since"/>, and what is known about it.</summary>
    public IReadOnlyDictionary<string, PendingSendState> ReadSince(DateTime since)
    {
        try
        {
            if (!File.Exists(FilePath))
                return new Dictionary<string, PendingSendState>(StringComparer.Ordinal);

            return FoldSince(File.ReadAllLines(FilePath), since);
        }
        catch (Exception ex)
        {
            // Unreadable is treated as "nothing recorded", which is the same answer a first run gives.
            // The caller cannot do better than that, and throwing here would take the run down.
            System.Diagnostics.Debug.WriteLine($"PendingSendStore.ReadSince failed: {ex.Message}");
            return new Dictionary<string, PendingSendState>(StringComparer.Ordinal);
        }
    }

    /// <summary>Every entry with a record for the given day, and what is known about it.</summary>
    public IReadOnlyDictionary<string, PendingSendState> ReadToday(DateTime day)
    {
        try
        {
            if (!File.Exists(FilePath))
                return new Dictionary<string, PendingSendState>(StringComparer.Ordinal);

            return FoldToday(File.ReadAllLines(FilePath), day);
        }
        catch (Exception ex)
        {
            // Unreadable is treated as "nothing recorded", which is the same answer a first run gives.
            // The caller cannot do better than that, and throwing here would take the run down.
            System.Diagnostics.Debug.WriteLine($"PendingSendStore.ReadToday failed: {ex.Message}");
            return new Dictionary<string, PendingSendState>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Drops records from before the given day, so the file cannot grow without bound.
    /// <para>
    /// Yesterday's records can no longer affect anything - the cooldown is per day - so this only ever
    /// discards what has already stopped mattering. Written through a temporary file and moved into
    /// place, so an interrupted prune leaves the previous file intact rather than a half-written one.
    /// </para>
    /// </summary>
    public void PruneBefore(DateTime day)
    {
        var cutoff = day.Date;

        try
        {
            if (!File.Exists(FilePath))
                return;

            var kept = new List<string>();
            var dropped = 0;

            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split(Separator, 4);

                if (parts.Length < 3 || !long.TryParse(parts[1], out var ticks))
                {
                    // Malformed lines are dropped: they can never be read back anyway.
                    dropped++;
                    continue;
                }

                DateTime at;
                try
                {
                    at = new DateTime(ticks);
                }
                catch (ArgumentOutOfRangeException)
                {
                    dropped++;
                    continue;
                }

                if (at.Date < cutoff)
                    dropped++;
                else
                    kept.Add(line);
            }

            if (dropped == 0)
                return;

            lock (_writeLock)
            {
                var temp = FilePath + ".tmp";
                File.WriteAllLines(temp, kept);
                File.Move(temp, FilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PendingSendStore.PruneBefore failed: {ex.Message}");
        }
    }

    /// <summary>Removes the whole record, for tests and for a deliberate reset.</summary>
    public void Clear()
    {
        try
        {
            lock (_writeLock)
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PendingSendStore.Clear failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Appends one record and forces it out to disk.
    /// </summary>
    /// <remarks>
    /// <see cref="File.AppendAllText(string,string)"/> returns once the data has reached the operating
    /// system, which is not the same as reaching the disk - and the whole point of this file is to
    /// survive a process that is killed without warning. <c>Flush(true)</c> is what makes the record
    /// durable; a plain append would leave the fix as vulnerable as the write it replaces.
    /// </remarks>
    private void AppendDurably(string record)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            lock (_writeLock)
            {
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = System.Text.Encoding.UTF8.GetBytes(record + Environment.NewLine);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex)
        {
            // A failed record must not stop the run. The cost of not recording is a possible duplicate
            // next time; the cost of throwing here would be no messages sent at all.
            System.Diagnostics.Debug.WriteLine($"PendingSendStore.AppendDurably failed: {ex.Message}");
        }
    }
}
