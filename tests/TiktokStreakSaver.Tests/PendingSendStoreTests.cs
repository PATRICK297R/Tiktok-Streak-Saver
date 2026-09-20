using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// Covers the record that stops a run killed by the system from messaging the same friend twice.
/// <para>
/// The interesting cases are the ones a kill produces: an attempt with no result, records that survive
/// being read back off disk, and records from a previous day that must no longer count.
/// </para>
/// </summary>
public class PendingSendStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"streaksaver-pending-{Guid.NewGuid():N}.txt");

    private static readonly DateTime Today = new(2026, 9, 18, 9, 0, 0);

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
            if (File.Exists(_path + ".tmp"))
                File.Delete(_path + ".tmp");
        }
        catch
        {
            // Temp cleanup must not fail the run.
        }
    }

    private static string[] Lines(params string[] lines) => lines;

    // ─── Folding ──────────────────────────────────────────────────────────────

    [Fact]
    public void AnAttemptWithNoResultIsUnknown()
    {
        // The state a kill leaves behind: the message may have gone out and we will never know.
        var states = PendingSendStore.FoldToday(
            Lines(PendingSendStore.FormatAttempt(Today, "f1", "ana")), Today);

        Assert.Equal(PendingSendState.Unknown, states["f1"]);
    }

    [Fact]
    public void AnAttemptFollowedBySuccessIsSent()
    {
        var states = PendingSendStore.FoldToday(Lines(
            PendingSendStore.FormatAttempt(Today, "f1", "ana"),
            PendingSendStore.FormatSent(Today.AddSeconds(4), "f1")), Today);

        Assert.Equal(PendingSendState.Sent, states["f1"]);
    }

    [Fact]
    public void AnAttemptFollowedByFailureIsFailed()
    {
        // A definite failure means nothing was sent, so a retry later today has to be allowed.
        var states = PendingSendStore.FoldToday(Lines(
            PendingSendStore.FormatAttempt(Today, "f1", "ana"),
            PendingSendStore.FormatFailed(Today.AddSeconds(4), "f1")), Today);

        Assert.Equal(PendingSendState.Failed, states["f1"]);
    }

    [Fact]
    public void ARetryThatSucceedsReportsSentNotUnknown()
    {
        var states = PendingSendStore.FoldToday(Lines(
            PendingSendStore.FormatAttempt(Today, "f1", "ana"),
            PendingSendStore.FormatFailed(Today.AddSeconds(3), "f1"),
            PendingSendStore.FormatAttempt(Today.AddSeconds(6), "f1", "ana"),
            PendingSendStore.FormatSent(Today.AddSeconds(9), "f1")), Today);

        Assert.Equal(PendingSendState.Sent, states["f1"]);
    }

    [Fact]
    public void EachEntryIsFoldedSeparately()
    {
        var states = PendingSendStore.FoldToday(Lines(
            PendingSendStore.FormatAttempt(Today, "f1", "ana"),
            PendingSendStore.FormatSent(Today, "f1"),
            PendingSendStore.FormatAttempt(Today, "f2", "budi"),
            PendingSendStore.FormatAttempt(Today, "f3", "cita"),
            PendingSendStore.FormatFailed(Today, "f3")), Today);

        Assert.Equal(3, states.Count);
        Assert.Equal(PendingSendState.Sent, states["f1"]);
        Assert.Equal(PendingSendState.Unknown, states["f2"]);
        Assert.Equal(PendingSendState.Failed, states["f3"]);
    }

    // ─── Days ─────────────────────────────────────────────────────────────────

    [Fact]
    public void RecordsFromAnotherDayAreIgnored()
    {
        // The cooldown is per day: yesterday's interrupted send must not block today's.
        var yesterday = Today.AddDays(-1);

        var states = PendingSendStore.FoldToday(Lines(
            PendingSendStore.FormatAttempt(yesterday, "f1", "ana"),
            PendingSendStore.FormatSent(yesterday, "f2")), Today);

        Assert.Empty(states);
    }

    [Fact]
    public void PruneBeforeDropsEarlierDaysAndKeepsToday()
    {
        var store = new PendingSendStore(_path);
        store.MarkAttempted("old", "lama", Today.AddDays(-2));
        store.MarkSent("older", Today.AddDays(-1));
        store.MarkAttempted("now", "kini", Today);

        store.PruneBefore(Today);

        var states = store.ReadToday(Today);
        Assert.Single(states);
        Assert.Equal(PendingSendState.Unknown, states["now"]);
    }

    [Fact]
    public void PruneBeforeLeavesTheFileAloneWhenThereIsNothingToDrop()
    {
        var store = new PendingSendStore(_path);
        store.MarkSent("f1", Today);
        var before = File.ReadAllText(_path);

        store.PruneBefore(Today);

        Assert.Equal(before, File.ReadAllText(_path));
    }

    // ─── Durability and round trips ───────────────────────────────────────────

    [Fact]
    public void AnAttemptSurvivesBeingWrittenAndReadBack()
    {
        // This is the whole point: the record has to be on disk before the message is sent, so that a
        // process killed moments later still finds it.
        var store = new PendingSendStore(_path);
        store.MarkAttempted("f1", "ana", Today);

        var reopened = new PendingSendStore(_path);
        Assert.Equal(PendingSendState.Unknown, reopened.ReadToday(Today)["f1"]);
    }

    [Fact]
    public void SuccessOverridesTheAttemptOnDisk()
    {
        var store = new PendingSendStore(_path);
        store.MarkAttempted("f1", "ana", Today);
        store.MarkSent("f1", Today.AddSeconds(5));

        Assert.Equal(PendingSendState.Sent, new PendingSendStore(_path).ReadToday(Today)["f1"]);
    }

    [Fact]
    public void AppendingDoesNotDisturbEarlierRecords()
    {
        var store = new PendingSendStore(_path);
        store.MarkSent("f1", Today);
        store.MarkAttempted("f2", "budi", Today);
        store.MarkFailed("f3", Today);

        var states = store.ReadToday(Today);
        Assert.Equal(PendingSendState.Sent, states["f1"]);
        Assert.Equal(PendingSendState.Unknown, states["f2"]);
        Assert.Equal(PendingSendState.Failed, states["f3"]);
    }

    [Fact]
    public void ReadingWithNoFileYetReportsNothing()
    {
        var store = new PendingSendStore(_path);
        Assert.Empty(store.ReadToday(Today));
    }

    [Fact]
    public void ClearRemovesTheRecord()
    {
        var store = new PendingSendStore(_path);
        store.MarkAttempted("f1", "ana", Today);

        store.Clear();

        Assert.Empty(store.ReadToday(Today));
    }

    // ─── Malformed input ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("A|notanumber|f1")]
    [InlineData("A|")]
    [InlineData("A|123|")]
    public void MalformedLinesAreIgnoredRatherThanGuessedAt(string line)
    {
        var states = PendingSendStore.FoldToday(Lines(line), Today);
        Assert.Empty(states);
    }

    [Fact]
    public void NullAndEmptyInputsAreHandled()
    {
        Assert.Empty(PendingSendStore.FoldToday(null, Today));
        Assert.Empty(PendingSendStore.FoldToday(Lines("", "  "), Today));
    }

    [Fact]
    public void AnUnrecognisedKindDoesNotTurnIntoSafeToSend()
    {
        // A record this build does not understand must not be read as "nothing happened here".
        var states = PendingSendStore.FoldToday(
            Lines(PendingSendStore.FormatAttempt(Today, "f1", "ana"), $"Z|{Today.Ticks}|f1"), Today);

        Assert.Equal(PendingSendState.Unknown, states["f1"]);
    }

    [Fact]
    public void AGroupNameContainingTheSeparatorIsKeptWhole()
    {
        // Group names are free text - "FIVE MULTIPLE|SEASON 6" is a real one - so the username field has
        // to survive the separator appearing inside it.
        var line = PendingSendStore.FormatAttempt(Today, "f1", "FIVE MULTIPLE|SEASON 6");

        Assert.Contains("FIVE MULTIPLE|SEASON 6", line);
        Assert.Equal(PendingSendState.Unknown, PendingSendStore.FoldToday(Lines(line), Today)["f1"]);
    }

    [Fact]
    public void APathIsRequired()
    {
        Assert.Throws<ArgumentException>(() => new PendingSendStore("  "));
    }
}
