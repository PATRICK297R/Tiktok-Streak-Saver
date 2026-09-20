using System.Text.Json;
using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class AppBackupCodecTests
{
    private static AppBackup SampleBackup() => new()
    {
        AppVersion = "2.1.9",
        ExportedAt = new DateTime(2026, 9, 17, 8, 30, 0),
        SessionDisplayName = "Streak User",
        Friends = new List<FriendConfig>
        {
            new() { Id = "f1", Username = "sari", DisplayName = "Sari", CustomMessage = "Hai {name}!" },
            new() { Id = "g1", Username = string.Empty, DisplayName = "Keluarga", IsGroup = true, IsEnabled = false }
        },
        Settings = new BackupSettings
        {
            MessageText = "Keeping our streak alive!",
            RandomizeNormalMessages = true,
            Scheduled = true,
            UseFixedTime = true,
            FixedTimeHour = 9,
            FixedTimeMinute = 30,
            FixedDailyTimes = new List<string> { "09:00", "21:00" },
            IntervalHours = 12,
            SkipUnreachableUsers = true,
            SendOnBatteryLow = false,
            StreakRiskAlerts = true,
            StreakRiskWarningHour = 20,
            DryRun = true,
            LastRunTime = new DateTime(2026, 9, 16, 21, 5, 0)
        },
        RunHistory = new List<StreakRunResult>
        {
            new()
            {
                RunTime = new DateTime(2026, 9, 16, 21, 0, 0),
                Duration = TimeSpan.FromSeconds(12.3),
                Success = true,
                FriendResults = new List<FriendMessageResult>
                {
                    new() { FriendId = "f1", Username = "sari", Success = true, Verified = true, Timestamp = new DateTime(2026, 9, 16, 21, 0, 5) }
                }
            }
        }
    };

    [Fact]
    public void ABackupSurvivesTheRoundTrip()
    {
        var json = AppBackupCodec.Serialize(SampleBackup());

        Assert.True(AppBackupCodec.TryDeserialize(json, out var restored, out var error), error);

        Assert.NotNull(restored);
        Assert.Equal("2.1.9", restored!.AppVersion);
        Assert.Equal(new DateTime(2026, 9, 17, 8, 30, 0), restored.ExportedAt);
        Assert.Equal("Streak User", restored.SessionDisplayName);
        Assert.Equal(2, restored.Friends.Count);
        Assert.Equal("Hai {name}!", restored.Friends[0].CustomMessage);
        Assert.True(restored.Friends[1].IsGroup);
        Assert.False(restored.Friends[1].IsEnabled);
    }

    [Fact]
    public void SettingsSurviveTheRoundTrip()
    {
        var json = AppBackupCodec.Serialize(SampleBackup());
        Assert.True(AppBackupCodec.TryDeserialize(json, out var restored, out _));

        var s = restored!.Settings;
        Assert.Equal("Keeping our streak alive!", s.MessageText);
        Assert.True(s.RandomizeNormalMessages);
        Assert.True(s.Scheduled);
        Assert.True(s.UseFixedTime);
        Assert.Equal(9, s.FixedTimeHour);
        Assert.Equal(30, s.FixedTimeMinute);
        Assert.Equal(new List<string> { "09:00", "21:00" }, s.FixedDailyTimes);
        Assert.Equal(12, s.IntervalHours);
        Assert.True(s.SkipUnreachableUsers);
        Assert.False(s.SendOnBatteryLow);
        Assert.True(s.StreakRiskAlerts);
        Assert.Equal(20, s.StreakRiskWarningHour);
        Assert.True(s.DryRun);
        Assert.Equal(new DateTime(2026, 9, 16, 21, 5, 0), s.LastRunTime);
    }

    [Fact]
    public void RunHistorySurvivesIncludingTheDurationAndVerification()
    {
        // Duration is written in the app's own ISO form, which is what the shared converter reads.
        var json = AppBackupCodec.Serialize(SampleBackup());
        Assert.True(AppBackupCodec.TryDeserialize(json, out var restored, out _));

        var run = Assert.Single(restored!.RunHistory);
        Assert.Equal(TimeSpan.FromSeconds(12.3), run.Duration);
        Assert.Equal(run.RunTime.AddSeconds(5), run.FriendResults[0].Timestamp);
        Assert.True(run.FriendResults[0].Verified);
    }

    [Fact]
    public void TheFormatVersionIsWrittenSoANewerFileCanBeRefused()
    {
        var json = AppBackupCodec.Serialize(new AppBackup());

        using var document = JsonDocument.Parse(json);
        Assert.Equal(AppBackup.CurrentFormatVersion, document.RootElement.GetProperty("FormatVersion").GetInt32());
    }

    [Fact]
    public void AFileFromANewerBuildIsRefusedWithAnExplanation()
    {
        var json = AppBackupCodec.Serialize(new AppBackup { FormatVersion = AppBackup.CurrentFormatVersion + 1 });

        Assert.False(AppBackupCodec.TryDeserialize(json, out var backup, out var error));
        Assert.Null(backup);
        Assert.Contains("newer version", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyFileIsRefused(string? json)
    {
        Assert.False(AppBackupCodec.TryDeserialize(json, out _, out var error));
        Assert.Equal("The file is empty.", error);
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("[\"a\", \"b\"]")]
    [InlineData("{\"Friends\": \"not a list\"}")]
    public void SomethingThatIsNotABackupIsRefused(string json)
    {
        Assert.False(AppBackupCodec.TryDeserialize(json, out var backup, out var error));
        Assert.Null(backup);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void AHandEditedFileWithMissingCollectionsStillLoads()
    {
        // Only the fields the user chose to keep, which is what a hand-written file looks like.
        const string json = """
            { "FormatVersion": 1, "Friends": [ { "Id": "f1", "Username": "sari" } ] }
            """;

        Assert.True(AppBackupCodec.TryDeserialize(json, out var backup, out var error), error);

        Assert.Single(backup!.Friends);
        Assert.NotNull(backup.Settings);
        Assert.NotNull(backup.Settings.FixedDailyTimes);
        Assert.NotNull(backup.RunHistory);
        Assert.Equal(string.Empty, backup.Settings.MessageText);
        Assert.Equal(string.Empty, backup.AppVersion);
    }

    [Fact]
    public void AFileWithoutAFormatVersionIsTreatedAsTheCurrentOne()
    {
        // The property keeps its initialiser when the key is absent, so an older or hand-written
        // file is not rejected for a missing field.
        Assert.True(AppBackupCodec.TryDeserialize("{\"Friends\": []}", out var backup, out var error), error);

        Assert.Equal(AppBackup.CurrentFormatVersion, backup!.FormatVersion);
    }

    [Fact]
    public void TheSummaryCountsFriendsAndGroupsSeparately()
    {
        var summary = AppBackupCodec.Summarize(SampleBackup());

        Assert.Equal(1, summary.Friends);
        Assert.Equal(1, summary.Groups);
        Assert.Equal("2.1.9", summary.AppVersion);
        Assert.Equal(1, summary.RunHistoryCount);
        Assert.Equal(new DateTime(2026, 9, 17, 8, 30, 0), summary.ExportedAt);
    }

    [Fact]
    public void ASummaryWithoutATimestampSaysSoRatherThanShowingYearOne()
    {
        var summary = AppBackupCodec.Summarize(new AppBackup { ExportedAt = default });

        Assert.Equal("unknown date", summary.ExportedAtText);
    }

    [Fact]
    public void AFileWithNoFriendsStillSummarizesCleanly()
    {
        var summary = AppBackupCodec.Summarize(new AppBackup { Friends = null!, RunHistory = null!, Settings = null! });

        Assert.Equal(0, summary.Friends);
        Assert.Equal(0, summary.Groups);
        Assert.Equal(0, summary.RunHistoryCount);
    }
}
