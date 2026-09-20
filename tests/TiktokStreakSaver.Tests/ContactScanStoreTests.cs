using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class ContactScanStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"streaksaver-scantest-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch
        {
            // Temp cleanup must not fail the run.
        }
    }

    private static ContactScanSnapshot Snapshot(
        ContactScanPhase phase = ContactScanPhase.Running,
        string message = "",
        int items = 0) => new()
    {
        Phase = phase,
        Message = message,
        Attempts = items + 1,
        UpdatedAt = DateTime.Now,
        Items = Enumerable.Range(1, items)
            .Select(i => new ContactScanItemDto { Username = $"user{i}", DisplayName = $"Chat {i}" })
            .ToList()
    };

    [Fact]
    public void PublishingWritesTheSnapshotToDisk()
    {
        var store = new ContactScanStore(_path);

        store.Publish(Snapshot(items: 3));

        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void ASnapshotWithNoFilePathIsRejected()
        => Assert.Throws<ArgumentException>(() => new ContactScanStore(" "));

    /// <summary>
    /// The whole reason the file exists: the scan runs in a service, the app can be closed, and the
    /// results have to still be there when the page is opened again.
    /// </summary>
    [Fact]
    public void AFreshStoreReadsWhatTheLastOneWrote()
    {
        new ContactScanStore(_path).Publish(Snapshot(ContactScanPhase.Finished, items: 5));

        var reloaded = new ContactScanStore(_path).Read();

        Assert.NotNull(reloaded);
        Assert.Equal(ContactScanPhase.Finished, reloaded!.Phase);
        Assert.Equal(5, reloaded.Items.Count);
        Assert.Equal("user3", reloaded.Items[2].Username);
        Assert.Equal("Chat 3", reloaded.Items[2].DisplayName);
    }

    [Fact]
    public void AFreshStoreWithNoFileReadsNothing()
        => Assert.Null(new ContactScanStore(_path).Read());

    /// <summary>
    /// A file left half-written, or written by an older build, must not stop the importer from working.
    /// </summary>
    [Fact]
    public void AnUnreadableFileReadsAsNothingRatherThanThrowing()
    {
        File.WriteAllText(_path, "{ this is not json");

        Assert.Null(new ContactScanStore(_path).Read());
    }

    [Fact]
    public void AnEmptyFileReadsAsNothing()
    {
        File.WriteAllText(_path, string.Empty);

        Assert.Null(new ContactScanStore(_path).Read());
    }

    /// <summary>
    /// The page watches the store while the service writes to it, so this process's copy has to win: the
    /// file may be a couple of seconds behind, and after a new scan starts it still holds the previous
    /// scan's results.
    /// </summary>
    [Fact]
    public void TheInMemorySnapshotWinsOverTheFile()
    {
        var store = new ContactScanStore(_path, TimeSpan.FromHours(1));
        store.Publish(Snapshot(message: "on disk"));
        store.Publish(Snapshot(message: "in memory"));

        Assert.Equal("in memory", store.Current!.Message);
        Assert.Equal("in memory", store.Read()!.Message);

        // The file was deliberately not rewritten, which is what proves memory is being preferred rather
        // than the two simply agreeing.
        Assert.Equal("on disk", new ContactScanStore(_path).Read()!.Message);
    }

    /// <summary>
    /// The scanner reports on every step it takes. Writing the whole result set each time would mean tens
    /// of megabytes of flash writes for one import of a long friend list.
    /// </summary>
    [Fact]
    public void ARunningSnapshotIsNotRewrittenInsideTheInterval()
    {
        var store = new ContactScanStore(_path, TimeSpan.FromHours(1));
        store.Publish(Snapshot(message: "first", items: 2));
        store.Publish(Snapshot(message: "second", items: 40));

        var onDisk = new ContactScanStore(_path).Read();

        Assert.Equal("first", onDisk!.Message);
        Assert.Equal(2, onDisk.Items.Count);
    }

    [Fact]
    public void ARunningSnapshotIsRewrittenOnceTheIntervalHasPassed()
    {
        var store = new ContactScanStore(_path, TimeSpan.Zero);
        store.Publish(Snapshot(message: "first"));
        store.Publish(Snapshot(message: "second"));

        Assert.Equal("second", new ContactScanStore(_path).Read()!.Message);
    }

    /// <summary>
    /// A stopped scan is the last word on it. Leaving the file describing a scan as still running is the
    /// one thing that would make the page wait for a scan that has already ended.
    /// </summary>
    [Fact]
    public void AStoppedSnapshotIsAlwaysWrittenEvenInsideTheInterval()
    {
        var store = new ContactScanStore(_path, TimeSpan.FromHours(1));
        store.Publish(Snapshot(ContactScanPhase.Running, message: "working"));
        store.Publish(Snapshot(ContactScanPhase.Finished, items: 24));

        var onDisk = new ContactScanStore(_path).Read();

        Assert.Equal(ContactScanPhase.Finished, onDisk!.Phase);
        Assert.Equal(24, onDisk.Items.Count);
    }

    [Fact]
    public void ClearingForgetsTheSnapshotEverywhere()
    {
        var store = new ContactScanStore(_path);
        store.Publish(Snapshot(items: 3));

        store.Clear();

        Assert.Null(store.Current);
        Assert.Null(store.Read());
        Assert.False(File.Exists(_path));
    }

    /// <summary>
    /// Publishing null is how a new scan starts from nothing; it has to leave nothing behind, or the new
    /// scan would inherit the previous one's chats.
    /// </summary>
    [Fact]
    public void PublishingNullClearsWhatWasThereBefore()
    {
        var store = new ContactScanStore(_path);
        store.Publish(Snapshot(ContactScanPhase.Finished, items: 9));

        store.Publish(null);

        Assert.Null(store.Read());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void ClearingWhenThereIsNothingToClearIsHarmless()
    {
        var store = new ContactScanStore(_path);

        store.Clear();

        Assert.Null(store.Read());
    }
}
