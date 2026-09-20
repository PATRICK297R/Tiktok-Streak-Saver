namespace TiktokStreakSaver.Services;

using System.Text.Json;

/// <summary>
/// Where the chat import's progress lives, so that a page can watch a scan it did not start and a scan
/// can outlive the page that asked for it.
/// <para>
/// Two places, for two reasons. The in-memory copy is what makes the page's progress live: the service
/// and the page are in the same process, so reading this is enough. The file is what makes the results
/// survive the app being closed - the whole point of running the scan in a service - and it is also the
/// only thing left if the process is killed.
/// </para>
/// <para>
/// The file is written at most once every <see cref="DefaultFileWriteInterval"/>. The scanner reports on
/// every step it takes, so a scan of a few hundred chats produces a report roughly every second; writing
/// the whole result set each time would mean tens of megabytes of flash writes for one import, and the
/// only reader that needs second-by-second freshness is in the same process and uses the memory copy.
/// Snapshots that are no longer running are always written, because that is the write that matters.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested against a temp file.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public sealed class ContactScanStore
{
    public static readonly TimeSpan DefaultFileWriteInterval = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly TimeSpan _fileWriteInterval;

    private ContactScanSnapshot? _current;
    private DateTime _lastWriteAt = DateTime.MinValue;

    public ContactScanStore(string filePath, TimeSpan? fileWriteInterval = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A snapshot file path is required.", nameof(filePath));

        FilePath = filePath;
        _fileWriteInterval = fileWriteInterval ?? DefaultFileWriteInterval;
    }

    public string FilePath { get; }

    /// <summary>The newest snapshot this process has published, or null if it has published none.</summary>
    public ContactScanSnapshot? Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    /// <summary>
    /// Publishes a snapshot to the memory copy, and to the file when it is due.
    /// <para>
    /// A null snapshot clears both, which is how a finished import is dismissed.
    /// </para>
    /// </summary>
    public void Publish(ContactScanSnapshot? snapshot)
    {
        lock (_lock)
        {
            _current = snapshot;

            if (snapshot is null)
            {
                TryDelete();
                _lastWriteAt = DateTime.MinValue;
                return;
            }

            // A stopped snapshot is the last word on the scan, so it is written even if the interval has
            // not elapsed - otherwise the file could be left describing a scan as still running.
            if (!snapshot.IsRunning || DateTime.Now - _lastWriteAt >= _fileWriteInterval)
            {
                TryWrite(snapshot);
                _lastWriteAt = DateTime.Now;
            }
        }
    }

    /// <summary>
    /// The newest snapshot known, preferring this process's copy.
    /// <para>
    /// The file is only consulted when memory has nothing, which is what a freshly started process looks
    /// like. Preferring memory matters when a new scan has just begun: the file still holds the previous
    /// scan's results for a moment, and returning those would show the user the wrong thing.
    /// </para>
    /// </summary>
    public ContactScanSnapshot? Read()
    {
        lock (_lock)
        {
            if (_current is not null)
                return _current;
        }

        return TryRead();
    }

    /// <summary>Forgets the current scan in memory and on disk.</summary>
    public void Clear() => Publish(null);

    private void TryWrite(ContactScanSnapshot snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot, AppJsonContext.Default.ContactScanSnapshot));
        }
        catch (Exception ex)
        {
            // Progress reporting must never take the scan down with it.
            System.Diagnostics.Debug.WriteLine($"ContactScanStore.TryWrite failed: {ex.Message}");
        }
    }

    private ContactScanSnapshot? TryRead()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize(json, AppJsonContext.Default.ContactScanSnapshot);
        }
        catch (Exception ex)
        {
            // A file that cannot be read is treated as no file at all: the caller then starts a fresh
            // scan, which is the right outcome, rather than being unable to do anything.
            System.Diagnostics.Debug.WriteLine($"ContactScanStore.TryRead failed: {ex.Message}");
            return null;
        }
    }

    private void TryDelete()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ContactScanStore.TryDelete failed: {ex.Message}");
        }
    }
}
