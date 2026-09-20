using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class BackupMergePolicyTests
{
    private static FriendConfig Friend(string username, string? id = null, bool enabled = true) =>
        new() { Id = id ?? username, Username = username, DisplayName = username, IsEnabled = enabled };

    private static FriendConfig Group(string name) =>
        new() { Id = "g-" + name, Username = string.Empty, DisplayName = name, IsGroup = true };

    private static string[] Usernames(IEnumerable<FriendConfig> list) =>
        list.Select(f => f.IsGroup ? "G:" + f.DisplayName : f.Username).ToArray();

    [Fact]
    public void RestoringIntoAnEmptyListAddsEverything()
    {
        // The reinstall case: merge and replace are the same, which is the point of merging by
        // default.
        var incoming = new List<FriendConfig> { Friend("a"), Friend("b") };

        var result = BackupMergePolicy.Merge(new List<FriendConfig>(), incoming);

        Assert.Equal(new[] { "a", "b" }, Usernames(result.Friends));
        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void EntriesAlreadyOnTheDeviceAreSkipped()
    {
        var current = new List<FriendConfig> { Friend("a") };
        var incoming = new List<FriendConfig> { Friend("a"), Friend("b") };

        var result = BackupMergePolicy.Merge(current, incoming);

        Assert.Equal(new[] { "a", "b" }, Usernames(result.Friends));
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void TheExistingListKeepsItsOrderAndRestoredEntriesGoLast()
    {
        // Order is the send queue, so a restore must not reshuffle what the user arranged.
        var current = new List<FriendConfig> { Friend("z"), Friend("a") };
        var incoming = new List<FriendConfig> { Friend("a"), Friend("m") };

        var result = BackupMergePolicy.Merge(current, incoming);

        Assert.Equal(new[] { "z", "a", "m" }, Usernames(result.Friends));
    }

    [Fact]
    public void AMatchingIdIsEnoughToTreatEntriesAsTheSame()
    {
        // The username was corrected on the device after the backup was written. The id still
        // identifies the entry, so the stale copy must not come back as a second row.
        var current = new List<FriendConfig> { Friend("newname", id: "shared-id") };
        var incoming = new List<FriendConfig> { Friend("oldname", id: "shared-id") };

        var result = BackupMergePolicy.Merge(current, incoming);

        Assert.Equal(new[] { "newname" }, Usernames(result.Friends));
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void EntriesWithoutIdsFallBackToMatchingByIdentity()
    {
        var current = new List<FriendConfig> { Friend("sari", id: string.Empty) };
        var incoming = new List<FriendConfig> { Friend("SARI", id: string.Empty) };

        var result = BackupMergePolicy.Merge(current, incoming);

        Assert.Single(result.Friends);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void AFriendAndAGroupWithTheSameNameAreNotTheSameEntry()
    {
        // A group and a person can share a name, and they are messaged differently.
        var current = new List<FriendConfig> { Friend("keluarga", id: "f1") };
        var incoming = new List<FriendConfig>
        {
            new() { Id = "g1", Username = "keluarga", DisplayName = "keluarga", IsGroup = true }
        };

        var result = BackupMergePolicy.Merge(current, incoming);

        Assert.Equal(2, result.Friends.Count);
        Assert.Equal(1, result.Added);
    }

    [Fact]
    public void EntriesThatNameNothingAreIgnored()
    {
        var current = new List<FriendConfig> { Friend("a") };
        var incoming = new List<FriendConfig>
        {
            new() { Id = "blank", Username = "   ", DisplayName = "   " },
            new() { Id = "group-blank", DisplayName = "  ", IsGroup = true },
            Friend("b")
        };

        var result = BackupMergePolicy.Merge(current, incoming);

        Assert.Equal(new[] { "a", "b" }, Usernames(result.Friends));
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void TheSameEntryTwiceInOneBackupIsCollapsed()
    {
        var incoming = new List<FriendConfig> { Friend("a"), Friend("a") };

        var result = BackupMergePolicy.Merge(new List<FriendConfig>(), incoming);

        Assert.Single(result.Friends);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void NullsOnEitherSideAreHandled()
    {
        Assert.Empty(BackupMergePolicy.Merge(null, null).Friends);
        Assert.Single(BackupMergePolicy.Merge(null, new List<FriendConfig> { Friend("a") }).Friends);
        Assert.Single(BackupMergePolicy.Merge(new List<FriendConfig> { Friend("a") }, null).Friends);
    }

    [Fact]
    public void GroupsSurviveTheRoundTripInOrder()
    {
        var current = new List<FriendConfig> { Group("Kerja") };
        var incoming = new List<FriendConfig> { Group("Kerja"), Friend("sari"), Group("Keluarga") };

        var result = BackupMergePolicy.Merge(current, incoming);

        Assert.Equal(new[] { "G:Kerja", "sari", "G:Keluarga" }, Usernames(result.Friends));
        Assert.Equal(2, result.Added);
    }

    [Fact]
    public void ReplaceUsesTheBackupListOnItsOwn()
    {
        var current = new List<FriendConfig> { Friend("device-only") };
        var incoming = new List<FriendConfig> { Friend("a"), Friend("b") };

        var replaced = BackupMergePolicy.Replace(incoming);

        Assert.Equal(new[] { "a", "b" }, Usernames(replaced));
        Assert.DoesNotContain(replaced, f => f.Username == "device-only");
    }

    [Fact]
    public void ReplaceStillDropsUnusableAndDuplicateEntries()
    {
        var incoming = new List<FriendConfig>
        {
            Friend("a"),
            Friend("a"),
            new() { Id = "blank", Username = "" }
        };

        var replaced = BackupMergePolicy.Replace(incoming);

        Assert.Single(replaced);
        Assert.Equal("a", replaced[0].Username);
    }

    [Fact]
    public void DisabledEntriesAreRestoredDisabled()
    {
        // Turning a friend back on behind the user's back would send a message they turned off.
        var incoming = new List<FriendConfig> { Friend("a", enabled: false) };

        var result = BackupMergePolicy.Merge(new List<FriendConfig>(), incoming);

        Assert.False(result.Friends[0].IsEnabled);
    }

    [Fact]
    public void CustomMessagesTravelWithTheEntry()
    {
        var incoming = new List<FriendConfig>
        {
            new() { Id = "a", Username = "a", DisplayName = "a", CustomMessage = "Hai {name}!" }
        };

        var result = BackupMergePolicy.Merge(new List<FriendConfig>(), incoming);

        Assert.Equal("Hai {name}!", result.Friends[0].CustomMessage);
        Assert.True(result.Friends[0].HasCustomMessage);
    }

    [Fact]
    public void TheSameChatUnderADifferentIdIsNotAddedTwice()
    {
        // A hand-edited backup file, or a contact import, carries fresh ids for chats that are
        // already on the list. Matching on the id alone would add a second row for one person.
        var current = new List<FriendConfig> { Friend("sari", id: "device-id") };
        var incoming = new List<FriendConfig> { Friend("sari", id: "a-brand-new-id") };

        var result = BackupMergePolicy.Merge(current, incoming);

        Assert.Single(result.Friends);
        Assert.Equal("device-id", result.Friends[0].Id);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void TheSameGroupUnderADifferentIdIsNotAddedTwice()
    {
        var current = new List<FriendConfig> { new() { Id = "d1", DisplayName = "Keluarga", IsGroup = true } };
        var incoming = new List<FriendConfig> { new() { Id = "other", DisplayName = "keluarga", IsGroup = true } };

        var result = BackupMergePolicy.Merge(current, incoming);

        Assert.Single(result.Friends);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void ReplaceCollapsesTheSameChatCarriedUnderTwoIds()
    {
        var incoming = new List<FriendConfig>
        {
            Friend("sari", id: "one"),
            Friend("sari", id: "two")
        };

        var replaced = BackupMergePolicy.Replace(incoming);

        Assert.Single(replaced);
    }

    [Fact]
    public void AnUnrelatedPersonIsStillAdded()
    {
        // Guards the multi-key matching from becoming so eager that nothing is ever added.
        var current = new List<FriendConfig> { Friend("sari", id: "device-id") };
        var incoming = new List<FriendConfig> { Friend("budi", id: "another-id") };

        var result = BackupMergePolicy.Merge(current, incoming);

        Assert.Equal(2, result.Friends.Count);
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Skipped);
    }
}
