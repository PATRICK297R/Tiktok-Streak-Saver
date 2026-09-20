using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class BackupTimesTests
{
    [Fact]
    public void TimesAreFormattedAsReadableClockText()
    {
        var formatted = BackupTimes.Format(new[] { new TimeSpan(9, 0, 0), new TimeSpan(21, 30, 0) });

        Assert.Equal(new[] { "09:00", "21:30" }, formatted);
    }

    [Fact]
    public void FormattingSortsAndDeDuplicates()
    {
        var formatted = BackupTimes.Format(new[]
        {
            new TimeSpan(21, 0, 0),
            new TimeSpan(9, 0, 0),
            new TimeSpan(21, 0, 0)
        });

        Assert.Equal(new[] { "09:00", "21:00" }, formatted);
    }

    [Fact]
    public void FormattingDropsSecondsSoTheFileIsStable()
    {
        var formatted = BackupTimes.Format(new[] { new TimeSpan(9, 15, 42) });

        Assert.Equal(new[] { "09:15" }, formatted);
    }

    [Fact]
    public void NullFormattingYieldsAnEmptyList()
    {
        Assert.Empty(BackupTimes.Format(null));
    }

    [Fact]
    public void TimesReadBackAsTheSameMoments()
    {
        var parsed = BackupTimes.Parse(new[] { "09:00", "21:00" });

        Assert.Equal(new[] { new TimeSpan(9, 0, 0), new TimeSpan(21, 0, 0) }, parsed);
    }

    [Fact]
    public void AFormatThenParseRoundTripPreservesTheSchedule()
    {
        // The property that matters: what was backed up is what comes back.
        var original = new[] { new TimeSpan(6, 45, 0), new TimeSpan(12, 0, 0), new TimeSpan(22, 15, 0) };

        var restored = BackupTimes.Parse(BackupTimes.Format(original));

        Assert.Equal(original, restored);
    }

    [Theory]
    [InlineData("24:00")]     // hour out of range
    [InlineData("09:60")]     // minute out of range
    [InlineData("-1:00")]
    [InlineData("nonsense")]
    [InlineData("09")]        // missing minutes
    [InlineData("09:00:00")]  // more parts than expected
    [InlineData("")]
    [InlineData("   ")]
    public void UnreadableEntriesAreDroppedRatherThanGuessedAt(string entry)
    {
        // Guessing an hour would send messages at the wrong time, so a bad entry is skipped.
        Assert.Empty(BackupTimes.Parse(new[] { entry }));
    }

    [Fact]
    public void AGoodEntrySurvivesAlongsideABadOne()
    {
        var parsed = BackupTimes.Parse(new[] { "09:00", "25:99", "21:00" });

        Assert.Equal(new[] { new TimeSpan(9, 0, 0), new TimeSpan(21, 0, 0) }, parsed);
    }

    [Fact]
    public void SurroundingWhitespaceIsTolerated()
    {
        var parsed = BackupTimes.Parse(new[] { " 09:00 ", " 21:00" });

        Assert.Equal(new[] { new TimeSpan(9, 0, 0), new TimeSpan(21, 0, 0) }, parsed);
    }

    [Fact]
    public void NullParsingYieldsAnEmptyList()
    {
        Assert.Empty(BackupTimes.Parse(null));
    }
}
