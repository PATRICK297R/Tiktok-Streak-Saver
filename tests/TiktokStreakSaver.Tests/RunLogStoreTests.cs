using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class RunLogStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"streaksaver-logtest-{Guid.NewGuid():N}.txt");

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

    [Fact]
    public void FormatKeepsTheHistoricLayout()
    {
        var line = RunLogStore.Format("SUCCESS", "@ana", "Message sent",
            new DateTime(2026, 9, 17, 8, 5, 9));

        Assert.Equal("[08:05:09] [SUCCESS] [@ana] Message sent", line);
    }

    [Fact]
    public void TrimKeepsTheNewestLinesWhenOverTheLineLimit()
    {
        var lines = new[] { "1", "2", "3", "4", "5" };

        var kept = RunLogStore.Trim(lines, maxLines: 3, maxBytes: long.MaxValue);

        Assert.Equal(new[] { "3", "4", "5" }, kept);
    }

    [Fact]
    public void TrimDropsOldestLinesUntilItFitsTheByteBudget()
    {
        // Each line plus the newline is 6 bytes in UTF-8.
        var lines = new[] { "aaaaa", "bbbbb", "ccccc" };

        var kept = RunLogStore.Trim(lines, maxLines: 100, maxBytes: 12);

        Assert.Equal(new[] { "bbbbb", "ccccc" }, kept);
    }

    [Fact]
    public void TrimAlwaysKeepsAtLeastOneLineEvenWhenItExceedsTheBudget()
    {
        var lines = new[] { new string('x', 500) };

        var kept = RunLogStore.Trim(lines, maxLines: 10, maxBytes: 4);

        Assert.Single(kept);
    }

    [Fact]
    public void TrimIgnoresNullInputAndNullLines()
    {
        Assert.Empty(RunLogStore.Trim(null, 10, 100));
        Assert.Equal(new[] { "a" }, RunLogStore.Trim(new string?[] { null, "a", null }, 10, 100));
    }

    [Fact]
    public void AppendThenReadRecentRoundTripsInOrder()
    {
        var store = new RunLogStore(_path);

        store.Append("first");
        store.Append("second");
        store.Append("third");

        Assert.Equal(new[] { "first", "second", "third" }, store.ReadRecent(10));
        Assert.True(store.HasEntries());
    }

    [Fact]
    public void ReadRecentReturnsOnlyTheTail()
    {
        var store = new RunLogStore(_path);
        for (var i = 1; i <= 10; i++)
            store.Append($"line {i}");

        Assert.Equal(new[] { "line 8", "line 9", "line 10" }, store.ReadRecent(3));
    }

    [Fact]
    public void SurvivesANewStoreInstancePointingAtTheSameFile()
    {
        // This is the whole point: the previous process wrote it, a fresh one reads it back.
        new RunLogStore(_path).Append("[08:00:00] [SYSTEM] [-] Preparing to send streaks...");

        var rehydrated = new RunLogStore(_path).ReadRecent(10);

        Assert.Single(rehydrated);
        Assert.Contains("Preparing to send streaks", rehydrated[0]);
    }

    [Fact]
    public void ClearRemovesTheFile()
    {
        var store = new RunLogStore(_path);
        store.Append("something");

        store.Clear();

        Assert.False(store.HasEntries());
        Assert.Empty(store.ReadRecent(10));
    }

    [Fact]
    public void RotatesWhenTheFilePassesTheByteLimit()
    {
        // Tiny budget so the rotation path is exercised without writing megabytes.
        var store = new RunLogStore(_path, maxLines: 500, maxBytes: 200);

        for (var i = 0; i < 100; i++)
            store.Append($"entry number {i:D3} padded to be reasonably long");

        var lines = store.ReadRecent(500);

        Assert.True(new FileInfo(_path).Length <= 200, "rotation should have trimmed the file");
        Assert.True(lines.Count < 100, "oldest entries should have been dropped");
        Assert.Contains("entry number 099", lines[^1]);
    }

    [Fact]
    public void ReadingAMissingFileReturnsEmptyInsteadOfThrowing()
    {
        var store = new RunLogStore(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt"));

        Assert.Empty(store.ReadRecent(10));
        Assert.False(store.HasEntries());
    }

    [Fact]
    public void AppendIgnoresEmptyEntriesAndRejectsABlankPath()
    {
        var store = new RunLogStore(_path);

        store.Append("");
        store.Append(null!);

        Assert.False(store.HasEntries());
        Assert.Throws<ArgumentException>(() => new RunLogStore("  "));
        Assert.Throws<ArgumentException>(() => new RunLogStore(null!));
    }
}
