using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class ContactScanSnapshotTests
{
    private static ContactScanSnapshot Running(int attempts = 0, int items = 0) => new()
    {
        Phase = ContactScanPhase.Running,
        Attempts = attempts,
        Items = Enumerable.Range(1, items)
            .Select(i => new ContactScanItemDto { Username = $"user{i}", DisplayName = $"Chat {i}" })
            .ToList()
    };

    [Fact]
    public void AStartingScanReadsAsWaitingForTheInbox()
        => Assert.Equal("Opening your inbox…", ContactScanSnapshot.ProgressText(new ContactScanSnapshot { Phase = ContactScanPhase.Starting }));

    [Fact]
    public void ARunningScanWithNothingFoundCountsTheChatsItHasOpened()
        => Assert.Equal("Reading your chats… opening chat 7", ContactScanSnapshot.ProgressText(Running(attempts: 7)));

    [Fact]
    public void ARunningScanReportsBothFoundAndChecked()
        => Assert.Equal("Reading your chats… 12 found, 18 checked", ContactScanSnapshot.ProgressText(Running(attempts: 18, items: 12)));

    [Fact]
    public void ARunningScanBeforeTheFirstChatSaysNothingElse()
        => Assert.Equal("Reading your chats…", ContactScanSnapshot.ProgressText(Running()));

    [Fact]
    public void NoSnapshotStillReadsAsWorking()
        => Assert.Equal("Reading your chats…", ContactScanSnapshot.ProgressText(null));

    [Fact]
    public void AFinishedScanCountsTheConversationsItRead()
        => Assert.Equal("Read 24 conversations.",
            ContactScanSnapshot.ProgressText(new ContactScanSnapshot { Phase = ContactScanPhase.Finished, Items = Running(items: 24).Items }));

    [Fact]
    public void OneConversationIsNotPluralised()
        => Assert.Equal("Read 1 conversation.",
            ContactScanSnapshot.ProgressText(new ContactScanSnapshot { Phase = ContactScanPhase.Finished, Items = Running(items: 1).Items }));

    [Fact]
    public void AFailedScanShowsWhyItFailed()
        => Assert.Equal("TikTok asked you to sign in again.",
            ContactScanSnapshot.ProgressText(new ContactScanSnapshot { Phase = ContactScanPhase.Failed, Message = "TikTok asked you to sign in again." }));

    [Fact]
    public void AFailedScanWithNoReasonStillSaysSomething()
        => Assert.Equal("The scan could not run.",
            ContactScanSnapshot.ProgressText(new ContactScanSnapshot { Phase = ContactScanPhase.Failed }));

    [Fact]
    public void TheFinishedNotificationSaysWhatToDoNext()
        => Assert.Equal("Read 24 chats — tap to review and add them.",
            ContactScanSnapshot.SummaryText(new ContactScanSnapshot { Phase = ContactScanPhase.Finished, Items = Running(items: 24).Items }));

    [Fact]
    public void AFinishedScanThatFoundNothingSaysSo()
        => Assert.Equal("No conversations were found in your inbox.",
            ContactScanSnapshot.SummaryText(new ContactScanSnapshot { Phase = ContactScanPhase.Finished }));

    [Fact]
    public void AStoppedScanKeepsWhatItReadAndSaysSo()
        => Assert.Equal("Stopped. 12 chats were read — tap to review them.",
            ContactScanSnapshot.SummaryText(new ContactScanSnapshot { Phase = ContactScanPhase.Cancelled, Items = Running(items: 12).Items }));

    [Fact]
    public void AStoppedScanThatReadNothingDoesNotOfferResults()
        => Assert.Equal("Stopped before anything could be read.",
            ContactScanSnapshot.SummaryText(new ContactScanSnapshot { Phase = ContactScanPhase.Cancelled }));

    [Fact]
    public void AFailedNotificationCarriesTheReason()
        => Assert.Equal("Could not read your chats: no internet connection.",
            ContactScanSnapshot.SummaryText(new ContactScanSnapshot { Phase = ContactScanPhase.Failed, Message = "no internet connection." }));

    [Fact]
    public void OnlyWorkingPhasesCountAsRunning()
    {
        Assert.True(new ContactScanSnapshot { Phase = ContactScanPhase.Starting }.IsRunning);
        Assert.True(new ContactScanSnapshot { Phase = ContactScanPhase.Running }.IsRunning);
        Assert.False(new ContactScanSnapshot { Phase = ContactScanPhase.Finished }.IsRunning);
        Assert.False(new ContactScanSnapshot { Phase = ContactScanPhase.Cancelled }.IsRunning);
        Assert.False(new ContactScanSnapshot { Phase = ContactScanPhase.Failed }.IsRunning);
        Assert.False(new ContactScanSnapshot { Phase = ContactScanPhase.Idle }.IsRunning);
    }

    [Fact]
    public void EveryPhaseThatHasStoppedIsStopped()
    {
        Assert.True(new ContactScanSnapshot { Phase = ContactScanPhase.Finished }.IsStopped);
        Assert.True(new ContactScanSnapshot { Phase = ContactScanPhase.Cancelled }.IsStopped);
        Assert.True(new ContactScanSnapshot { Phase = ContactScanPhase.Failed }.IsStopped);
        Assert.False(new ContactScanSnapshot { Phase = ContactScanPhase.Running }.IsStopped);
        Assert.False(new ContactScanSnapshot { Phase = ContactScanPhase.Idle }.IsStopped);
    }

    /// <summary>
    /// This state is only reachable after the app was closed mid-scan and reopened, and the point of it
    /// is that the page must not then wait forever on a scan that is no longer running.
    /// </summary>
    [Fact]
    public void AScanThatSaysItIsRunningButHasGoneQuietIsAbandoned()
    {
        var now = new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Local);
        var snapshot = Running(attempts: 5);
        snapshot.UpdatedAt = now.AddMinutes(-5);

        Assert.True(ContactScanSnapshot.IsAbandoned(snapshot, now, TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void AScanThatIsStillWritingIsNotAbandoned()
    {
        var now = new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Local);
        var snapshot = Running(attempts: 5);
        snapshot.UpdatedAt = now.AddSeconds(-10);

        Assert.False(ContactScanSnapshot.IsAbandoned(snapshot, now, TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void AStoppedScanIsNeverAbandonedHoweverOldItIs()
    {
        var now = new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Local);
        var snapshot = new ContactScanSnapshot { Phase = ContactScanPhase.Finished, UpdatedAt = now.AddDays(-3) };

        Assert.False(ContactScanSnapshot.IsAbandoned(snapshot, now, TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void NoSnapshotIsNotAbandoned()
        => Assert.False(ContactScanSnapshot.IsAbandoned(null, DateTime.Now, TimeSpan.FromMinutes(4)));

    /// <summary>
    /// The empty-scan explanation is written once, against the scanner's own report type. Handing the
    /// stored state over in that shape is what stops a second copy of those messages from existing.
    /// </summary>
    [Fact]
    public void TheStoredStateConvertsToTheShapeTheScannerUses()
    {
        var snapshot = new ContactScanSnapshot
        {
            Phase = ContactScanPhase.Finished,
            Attempts = 26,
            Items = Running(items: 24).Items,
            Log = new List<string> { "finished" },
            Url = "https://www.tiktok.com/messages",
            UserAgent = "ua"
        };

        var dto = snapshot.ToScanDto();

        Assert.True(dto.Done);
        Assert.Equal(26, dto.Attempts);
        Assert.Equal(24, dto.Items.Count);
        Assert.Equal("finished", dto.Log[^1]);
        Assert.Equal("https://www.tiktok.com/messages", dto.Url);
        Assert.Equal("ua", dto.UserAgent);
    }

    [Fact]
    public void AStoppedScanIsNotReportedAsDoneToTheReader()
        => Assert.False(new ContactScanSnapshot { Phase = ContactScanPhase.Cancelled }.ToScanDto().Done);

    [Fact]
    public void StoringNoChatsProducesNoChats()
        => Assert.Empty(ContactScanSnapshot.ItemsFrom(null));

    /// <summary>
    /// The service reads the inbox into app entries and stores them as chats; the page reads those back
    /// into app entries to build the review list. If that round trip lost or reshaped anything, an
    /// imported group would come back as a person, or a chat would come back with no username and be
    /// dropped - and the count the user was shown would be wrong.
    /// </summary>
    [Fact]
    public void ChatsSurviveTheRoundTripThroughStorage()
    {
        var scanned = new List<ContactScanItemDto>
        {
            new() { Username = "ana", DisplayName = "Ana", IsGroup = false },
            new() { Username = string.Empty, DisplayName = "FIVE MULTIPLE", IsGroup = true },
            new() { Username = "  bob  ", DisplayName = string.Empty, IsGroup = false }
        };

        var first = ContactScanReader.ToFriends(scanned);
        var second = ContactScanReader.ToFriends(ContactScanSnapshot.ItemsFrom(first));

        Assert.Equal(first.Count, second.Count);

        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Username, second[i].Username);
            Assert.Equal(first[i].DisplayName, second[i].DisplayName);
            Assert.Equal(first[i].IsGroup, second[i].IsGroup);
        }
    }

    /// <summary>A group is stored with no username at all, which is the field a person is matched by.</summary>
    [Fact]
    public void AStoredGroupKeepsItsNameAndHasNoUsername()
    {
        var stored = ContactScanSnapshot.ItemsFrom(ContactScanReader.ToFriends(
            new List<ContactScanItemDto> { new() { DisplayName = "Humas SMKN 1 TROWULAN", IsGroup = true } }));

        Assert.Single(stored);
        Assert.True(stored[0].IsGroup);
        Assert.Equal("Humas SMKN 1 TROWULAN", stored[0].DisplayName);
        Assert.Equal(string.Empty, stored[0].Username);
    }
}
