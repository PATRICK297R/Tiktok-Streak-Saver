namespace TiktokStreakSaver.Services;

/// <summary>
/// Append-only run log persisted to a file, so a run that dies before finishing can still be
/// diagnosed afterwards.
/// <para>
/// The log used to live only in a static in-memory list, which meant everything was lost the moment
/// the background service was killed - exactly the situation the code already anticipated
/// ("without CompleteService being called (e.g. system kill while WebView is loading)"). A killed
/// process left no trace at all, which made the hardest failures the least diagnosable.
/// </para>
/// <para>
/// The caller supplies the file path, which keeps this class free of MAUI and Android dependencies so
/// it can be unit tested directly against a temp file.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public sealed class RunLogStore
{
    public const int DefaultMaxLines = 500;

    /// <summary>
    /// Cap on the file size. 256 KB is far more than a single run produces, so normal use never
    /// rotates; the limit only exists so a stuck loop cannot grow the file without bound.
    /// </summary>
    public const long DefaultMaxBytes = 256 * 1024;

    private readonly int _maxLines;
    private readonly long _maxBytes;

    public RunLogStore(string filePath, int maxLines = DefaultMaxLines, long maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A log file path is required.", nameof(filePath));

        FilePath = filePath;
        _maxLines = Math.Max(1, maxLines);
        _maxBytes = Math.Max(1, maxBytes);
    }

    public string FilePath { get; }

    /// <summary>
    /// Renders one log line. Kept identical to the historic format so existing exports and any
    /// tooling reading them keep working.
    /// </summary>
    public static string Format(string phase, string username, string message, DateTime timestamp)
        => $"[{timestamp:HH:mm:ss}] [{phase}] [{username}] {message}";

    /// <summary>
    /// Keeps the newest lines within both limits. Line count is applied first, then bytes are dropped
    /// from the oldest end until the total fits, so the most recent context always survives.
    /// </summary>
    public static IReadOnlyList<string> Trim(IEnumerable<string>? lines, int maxLines, long maxBytes)
    {
        if (lines is null)
            return Array.Empty<string>();

        var kept = new List<string>();
        var countLimit = Math.Max(1, maxLines);

        foreach (var line in lines)
        {
            if (line is null)
                continue;

            kept.Add(line);

            if (kept.Count > countLimit)
                kept.RemoveAt(0);
        }

        var byteLimit = Math.Max(1, maxBytes);
        long total = 0;
        foreach (var line in kept)
            total += LineBytes(line);

        while (kept.Count > 1 && total > byteLimit)
        {
            total -= LineBytes(kept[0]);
            kept.RemoveAt(0);
        }

        return kept;
    }

    /// <summary>Appends one entry, rotating the file when it grows past the size limit.</summary>
    public void Append(string entry)
    {
        if (string.IsNullOrEmpty(entry))
            return;

        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.AppendAllText(FilePath, entry + Environment.NewLine);

            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > _maxBytes)
                Rotate();
        }
        catch (Exception ex)
        {
            // Logging must never take the caller down with it.
            System.Diagnostics.Debug.WriteLine($"RunLogStore.Append failed: {ex.Message}");
        }
    }

    public void Append(string phase, string username, string message, DateTime timestamp)
        => Append(Format(phase, username, message, timestamp));

    /// <summary>
    /// The newest <paramref name="max"/> lines, oldest first. Returns an empty list when the file is
    /// missing or unreadable.
    /// </summary>
    public List<string> ReadRecent(int max)
    {
        if (max <= 0)
            return new List<string>();

        try
        {
            if (!File.Exists(FilePath))
                return new List<string>();

            var lines = File.ReadAllLines(FilePath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            return Trim(lines, max, long.MaxValue).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RunLogStore.ReadRecent failed: {ex.Message}");
            return new List<string>();
        }
    }

    public bool HasEntries()
    {
        try
        {
            var info = new FileInfo(FilePath);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RunLogStore.Clear failed: {ex.Message}");
        }
    }

    private void Rotate()
    {
        var lines = File.ReadAllLines(FilePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var trimmed = Trim(lines, _maxLines, _maxBytes);

        File.WriteAllLines(FilePath, trimmed);
    }

    // UTF-8 is the encoding File.AppendAllText uses, so multi-byte characters are counted correctly.
    private static long LineBytes(string line)
        => System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
}
