using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class ContactScanReaderTests
{
    private static ContactScanItemDto User(string username, string displayName = "") =>
        new() { Username = username, DisplayName = displayName, IsGroup = false };

    private static ContactScanItemDto Group(string name) =>
        new() { Username = string.Empty, DisplayName = name, IsGroup = true };

    [Fact]
    public void TheScannersPayloadIsReadWithItsCamelCaseKeys()
    {
        const string json = """
            {"done":true,"attempts":4,"items":[
              {"username":"sari","displayName":"Sari","isGroup":false},
              {"username":"","displayName":"Keluarga","isGroup":true}
            ]}
            """;

        Assert.True(ContactScanReader.TryParse(json, out var scan, out var error), error);

        Assert.True(scan!.Done);
        Assert.Equal(4, scan.Attempts);
        Assert.Equal(2, scan.Items.Count);
        Assert.Equal("sari", scan.Items[0].Username);
        Assert.True(scan.Items[1].IsGroup);
    }

    [Fact]
    public void APayloadStillBeingBuiltIsReadable()
    {
        // The app polls while the scan runs, so a partial payload is the normal case, not an error.
        Assert.True(ContactScanReader.TryParse("{\"done\":false,\"items\":[]}", out var scan, out _));

        Assert.False(scan!.Done);
        Assert.Empty(scan.Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{\"items\":\"wrong type\"}")]
    public void UnreadableScanDataIsRefused(string? json)
    {
        Assert.False(ContactScanReader.TryParse(json, out var scan, out var error));
        Assert.Null(scan);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void APersonBecomesAFriend()
    {
        var friends = ContactScanReader.ToFriends(new[] { User("sari", "Sari") });

        var friend = Assert.Single(friends);
        Assert.Equal("sari", friend.Username);
        Assert.Equal("Sari", friend.DisplayName);
        Assert.False(friend.IsGroup);
        Assert.True(friend.IsEnabled);
    }

    [Fact]
    public void AHandleMissingItsAtSignIsStillAccepted()
    {
        // The scanner strips it, but the payload is scraped text and is not trusted to be tidy.
        var friends = ContactScanReader.ToFriends(new[] { User("@sari", "Sari") });

        Assert.Equal("sari", friends[0].Username);
    }

    [Fact]
    public void APersonWithOnlyAHandleUsesItAsTheName()
    {
        var friends = ContactScanReader.ToFriends(new[] { User("sari") });

        Assert.Equal("sari", friends[0].DisplayName);
    }

    [Fact]
    public void AGroupIsMatchedByNameWithNoHandle()
    {
        var friends = ContactScanReader.ToFriends(new[] { Group("Keluarga") });

        var group = Assert.Single(friends);
        Assert.True(group.IsGroup);
        Assert.Equal("Keluarga", group.DisplayName);
        Assert.Equal(string.Empty, group.Username);
    }

    [Fact]
    public void APersonWithNoHandleIsDropped()
    {
        // Sending navigates by handle, so an entry without one could never succeed.
        Assert.Empty(ContactScanReader.ToFriends(new[] { User("   ", "Someone") }));
    }

    [Fact]
    public void AGroupWithNoNameIsDropped()
    {
        // Groups are matched on their chat header, so a nameless one is unusable.
        Assert.Empty(ContactScanReader.ToFriends(new[] { Group("  ") }));
    }

    [Fact]
    public void TheSameChatFromTwoPassesOfTheScanIsKeptOnce()
    {
        // The scanner re-reads the same rows on every scroll pass, so repeats are guaranteed.
        var friends = ContactScanReader.ToFriends(new[] { User("sari", "Sari"), User("SARI", "Sari") });

        Assert.Single(friends);
    }

    [Fact]
    public void GroupsAndPeopleWithTheSameNameBothSurvive()
    {
        var friends = ContactScanReader.ToFriends(new[] { User("keluarga", "Keluarga"), Group("Keluarga") });

        Assert.Equal(2, friends.Count);
    }

    [Fact]
    public void ANameSpreadOverLinesIsFlattened()
    {
        var friends = ContactScanReader.ToFriends(new[] { User("sari", "Sa\nri") });

        Assert.Equal("Sa ri", friends[0].DisplayName);
    }

    [Fact]
    public void NullsAndEmptyListsYieldNothing()
    {
        Assert.Empty(ContactScanReader.ToFriends(null));
        Assert.Empty(ContactScanReader.ToFriends(Array.Empty<ContactScanItemDto>()));
    }

    // ─── Selecting what to import ───────────────────────────────────────────

    private static FriendConfig ExistingFriend(string username, string? id = null) =>
        new() { Id = id ?? "existing-" + username, Username = username, DisplayName = username };

    private static FriendConfig ExistingGroup(string name) =>
        new() { Id = "existing-group-" + name, Username = string.Empty, DisplayName = name, IsGroup = true };

    [Fact]
    public void SomeoneAlreadyOnTheListIsNotOfferedAgain()
    {
        // The important case: scanned entries carry fresh ids, so matching on the id alone would
        // offer every contact again on every import.
        var existing = new List<FriendConfig> { ExistingFriend("sari") };
        var scanned = ContactScanReader.ToFriends(new[] { User("sari", "Sari"), User("budi", "Budi") });

        var toAdd = ContactScanReader.SelectNew(existing, scanned);

        Assert.Single(toAdd);
        Assert.Equal("budi", toAdd[0].Username);
    }

    [Fact]
    public void ImportingTheSameScanTwiceAddsNothingTheSecondTime()
    {
        var scanned = ContactScanReader.ToFriends(new[] { User("sari", "Sari"), User("budi", "Budi") });

        var first = ContactScanReader.SelectNew(new List<FriendConfig>(), scanned);
        var second = ContactScanReader.SelectNew(first, scanned);

        Assert.Equal(2, first.Count);
        Assert.Empty(second);
    }

    [Fact]
    public void MatchingIgnoresCaseAndSurroundingSpace()
    {
        var existing = new List<FriendConfig> { ExistingFriend("sari") };
        var scanned = ContactScanReader.ToFriends(new[] { User("  SARI  ", "Sari") });

        Assert.Empty(ContactScanReader.SelectNew(existing, scanned));
    }

    [Fact]
    public void AnAlreadyKnownGroupIsNotOfferedAgain()
    {
        var existing = new List<FriendConfig> { ExistingGroup("Keluarga") };
        var scanned = ContactScanReader.ToFriends(new[] { Group("Keluarga") });

        Assert.Empty(ContactScanReader.SelectNew(existing, scanned));
    }

    [Fact]
    public void AGroupIsNotTreatedAsAnAlreadyKnownPersonOfTheSameName()
    {
        var existing = new List<FriendConfig> { ExistingFriend("keluarga") };
        var scanned = ContactScanReader.ToFriends(new[] { Group("keluarga") });

        var toAdd = ContactScanReader.SelectNew(existing, scanned);

        Assert.Single(toAdd);
        Assert.True(toAdd[0].IsGroup);
    }

    [Fact]
    public void EverythingIsNewAgainstAnEmptyList()
    {
        var scanned = ContactScanReader.ToFriends(new[] { User("a", "A"), User("b", "B"), Group("G") });

        Assert.Equal(3, ContactScanReader.SelectNew(new List<FriendConfig>(), scanned).Count);
    }

    [Fact]
    public void AScanThatFoundNothingOffersNothing()
    {
        Assert.Empty(ContactScanReader.SelectNew(new List<FriendConfig> { ExistingFriend("a") }, new List<FriendConfig>()));
        Assert.Empty(ContactScanReader.SelectNew(null, null));
    }

    // ─── What EvaluateJavaScriptAsync hands back ────────────────────────────

    /// <summary>
    /// Builds the raw value the way the platform does: the JSON encoding of a JavaScript string.
    /// Hand-writing the escaping would test the test rather than the code.
    /// </summary>
    private static string AsJavaScriptStringResult(string inner) =>
        System.Text.Json.JsonSerializer.Serialize(inner);

    [Fact]
    public void AnAndroidStyleQuotedResultIsUnwrapped()
    {
        var raw = AsJavaScriptStringResult("{\"done\":true,\"items\":[]}");

        // Sanity: the platform really did wrap and encode it. Note the escaping is not the same
        // everywhere - .NET's serializer writes \" as \u0022, while Android's WebView typically writes
        // \". Decoding the JSON handles both, which is precisely why this is not hand-parsed.
        Assert.StartsWith("\"", raw);
        Assert.NotEqual("{\"done\":true,\"items\":[]}", raw);
        Assert.EndsWith("\"", raw);

        var normalized = ContactScanReader.NormalizeJavaScriptResult(raw);

        Assert.Equal("{\"done\":true,\"items\":[]}", normalized);
        Assert.True(ContactScanReader.TryParse(normalized, out var scan, out _));
        Assert.True(scan!.Done);
    }

    [Fact]
    public void AnUnquotedResultIsPassedThrough()
    {
        const string raw = "{\"done\":true,\"items\":[]}";

        Assert.Equal(raw, ContactScanReader.NormalizeJavaScriptResult(raw));
    }

    [Fact]
    public void AUnicodeGroupNameSurvivesBothLayers()
    {
        // A group name with an emoji is common, and it has to come out of the double decoding intact.
        var raw = AsJavaScriptStringResult("{\"items\":[{\"displayName\":\"Keluarga \u2764\",\"isGroup\":true}]}");

        Assert.True(ContactScanReader.TryParse(ContactScanReader.NormalizeJavaScriptResult(raw), out var scan, out _));
        Assert.Equal("Keluarga \u2764", scan!.Items[0].DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void ANothingYetResultNormalizesToEmpty(string? raw)
    {
        // This is what every poll returns before the script has produced anything.
        Assert.Equal(string.Empty, ContactScanReader.NormalizeJavaScriptResult(raw));
    }

    [Fact]
    public void AResultThatOnlyLooksQuotedIsLeftAlone()
    {
        // Starts and ends with a quote but is not valid JSON inside one.
        var normalized = ContactScanReader.NormalizeJavaScriptResult("\"unterminated");

        Assert.False(string.IsNullOrWhiteSpace(normalized));
    }

    // ─── Explaining an empty scan ───────────────────────────────────────────

    [Fact]
    public void ThePageIsReportedWhenTheInboxIsWhereItEndedUp()
    {
        var scan = new ContactScanDto
        {
            Attempts = 12,
            Url = "https://www.tiktok.com/messages",
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0",
            Log = new List<string> { "Scan started", "Pass 12: 0 chats (none new)" }
        };

        var text = ContactScanReader.DescribeEmptyScan(scan);

        Assert.Contains("Steps taken: 12", text);
        Assert.Contains("Pass 12", text);
        Assert.Contains("Mozilla/5.0", text);
    }

    [Fact]
    public void APageThatIsNotTheInboxIsCalledOut()
    {
        // The useful distinction: this is not an empty inbox, it is the wrong page.
        var scan = new ContactScanDto
        {
            Attempts = 3,
            Url = "https://www.tiktok.com/login/phone-or-email/email"
        };

        var text = ContactScanReader.DescribeEmptyScan(scan);

        Assert.Contains("did not stay on the inbox", text);
        Assert.Contains("sign in again", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("login", text);
    }

    [Fact]
    public void AScannerThatNeverInspectedAnythingSaysSo()
    {
        var scan = new ContactScanDto { Attempts = 0, Url = "https://www.tiktok.com/messages" };

        var text = ContactScanReader.DescribeEmptyScan(scan);

        Assert.Contains("never managed to inspect", text);
    }

    [Fact]
    public void AScannerThatRanSaysSoWithoutBlamingAnEmptyInbox()
    {
        var scan = new ContactScanDto
        {
            Attempts = 5,
            Url = "https://www.tiktok.com/messages",
            Log = new List<string>()
        };

        var text = ContactScanReader.DescribeEmptyScan(scan);

        Assert.Contains("no conversation rows could be read", text);
        Assert.DoesNotContain("Last step", text);
    }

    [Fact]
    public void AMissingScanStateIsReportedAsSuch()
    {
        var text = ContactScanReader.DescribeEmptyScan(null);

        Assert.Contains("never reached", text);
    }

    [Fact]
    public void TheDiagnosticsReadTheScannerStateWithItsCamelCaseKeys()
    {
        const string json = """
            {"done":true,"attempts":9,"items":[],"url":"https://www.tiktok.com/messages",
             "ua":"Mozilla/5.0 Chrome/131","log":["Scan started","Pass 9: 0 chats"]}
            """;

        Assert.True(ContactScanReader.TryParse(json, out var scan, out var error), error);

        Assert.Equal(9, scan!.Attempts);
        Assert.Equal("https://www.tiktok.com/messages", scan.Url);
        Assert.Equal("Mozilla/5.0 Chrome/131", scan.UserAgent);
        Assert.Equal(2, scan.Log.Count);
    }

    [Fact]
    public void AMissingDiagnosticsBlockStillLoads()
    {
        // An older or partial payload must not make the whole poll fail.
        Assert.True(ContactScanReader.TryParse("{\"done\":false,\"items\":[]}", out var scan, out _));

        Assert.Empty(scan!.Log);
        Assert.Equal(string.Empty, scan.Url);
        Assert.Equal(string.Empty, scan.UserAgent);
    }
}
