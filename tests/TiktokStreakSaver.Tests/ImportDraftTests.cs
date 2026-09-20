using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// The review step between scanning and saving.
/// <para>
/// The point of the screen is that a correction reaches the list that is saved. A review whose edits are
/// dropped, or which saves an entry it warned about, is worse than no review at all - so what is checked
/// here is what ends up in <see cref="ImportDraft.ToAdd"/>, not what the row happens to display.
/// </para>
/// </summary>
public class ImportDraftTests
{
    private static FriendConfig Person(string username, string displayName = "") =>
        new() { Username = username, DisplayName = displayName, IsGroup = false };

    private static FriendConfig Group(string name) =>
        new() { Username = string.Empty, DisplayName = name, IsGroup = true };

    [Fact]
    public void EverythingFoundStartsSelected()
    {
        var draft = new ImportDraft([Person("sari"), Group("Keluarga")]);

        Assert.Equal(2, draft.Count);
        Assert.Equal(2, draft.IncludedCount);
        Assert.Equal(2, draft.ToAdd().Count);
    }

    [Fact]
    public void OnlyTheTickedEntriesAreAdded()
    {
        var draft = new ImportDraft([Person("sari"), Person("budi"), Group("Keluarga")]);

        draft.Entries[1].Include = false;

        var added = draft.ToAdd();

        Assert.Equal(2, added.Count);
        Assert.DoesNotContain(added, entry => entry.Username == "budi");
        Assert.Equal(2, draft.IncludedCount);
    }

    [Fact]
    public void SelectingAllAndNoneTogglesEveryRow()
    {
        var draft = new ImportDraft([Person("sari"), Group("Keluarga")]);

        draft.IncludeNone();
        Assert.Equal(0, draft.IncludedCount);
        Assert.Empty(draft.ToAdd());

        draft.IncludeAll();
        Assert.Equal(2, draft.IncludedCount);
        Assert.Equal(2, draft.ToAdd().Count);
    }

    [Fact]
    public void TheOrderOfTheScanIsKept()
    {
        // The order is the send queue, so a review that reshuffled it would change what the app does.
        var draft = new ImportDraft([Person("sari"), Person("budi"), Person("tono")]);

        Assert.Equal(["sari", "budi", "tono"], draft.ToAdd().Select(entry => entry.Username));
    }

    // ─── Correcting a chat that was read wrongly ─────────────────────────────

    [Fact]
    public void AGroupReadAsAPersonCanBePutRightAndIsThenSavedAsAGroup()
    {
        // The failure this screen exists for: the scan called it a person, and saving it that way would
        // look the chat up by one member's handle.
        var draft = new ImportDraft([Person("humas.smkn.1.trow", "Humas SMKN 1 TROWULAN")]);

        ContactEditRules.SetGroup(draft.Entries[0].Entry, true);

        var added = draft.ToAdd();

        Assert.Single(added);
        Assert.True(added[0].IsGroup);
        Assert.Equal("Humas SMKN 1 TROWULAN", added[0].DisplayName);
        Assert.Contains("Group", draft.Entries[0].Subtitle);
    }

    [Fact]
    public void APersonReadAsAGroupIsNotReadyUntilItsUsernameIsGiven()
    {
        var draft = new ImportDraft([Group("Rasya RaisKuker")]);

        ContactEditRules.SetGroup(draft.Entries[0].Entry, false);

        Assert.Equal(1, draft.BlockedCount);
        Assert.Empty(draft.ToAdd());
        Assert.Contains("needs a username", draft.Entries[0].Subtitle);

        Assert.True(ContactEditRules.TrySetUsername(draft.Entries[0].Entry, "virkun01", out _));

        Assert.Equal(0, draft.BlockedCount);
        Assert.Equal("virkun01", Assert.Single(draft.ToAdd()).Username);
    }

    [Fact]
    public void AnEntryThatIsNotReadyIsLeftOutAndSaysWhy()
    {
        var draft = new ImportDraft([Person("sari"), Person(string.Empty, "No Handle")]);

        var blocked = draft.DescribeBlocked();

        Assert.NotNull(blocked);
        Assert.Contains("No Handle", blocked);
        Assert.Single(draft.ToAdd());
    }

    [Fact]
    public void AReadySelectionBlocksNothing()
    {
        Assert.Null(new ImportDraft([Person("sari"), Group("Keluarga")]).DescribeBlocked());
    }

    [Fact]
    public void AnUntickedEntryThatIsNotReadyDoesNotBlock()
    {
        // Untick it and the warning goes with it: the user has already dealt with the row.
        var draft = new ImportDraft([Person("sari"), Person(string.Empty, "No Handle")]);

        draft.Entries[1].Include = false;

        Assert.Null(draft.DescribeBlocked());
        Assert.Single(draft.ToAdd());
    }

    [Fact]
    public void SeveralUnreadyEntriesAreNamedNotJustCounted()
    {
        var draft = new ImportDraft([Person(string.Empty, "One"), Person(string.Empty, "Two")]);

        var blocked = draft.DescribeBlocked();

        Assert.NotNull(blocked);
        Assert.Contains("One", blocked);
        Assert.Contains("Two", blocked);
    }

    // ─── What the rows show ──────────────────────────────────────────────────

    [Fact]
    public void TheHeadingFallsBackToTheHandleWhenThereIsNoName()
    {
        Assert.Equal("sari", new ImportDraftEntry(Person("sari")).Title);
        Assert.Equal("Sari", new ImportDraftEntry(Person("sari", "Sari")).Title);
    }

    [Fact]
    public void APersonWithNoHandleSaysSoOnItsRow()
    {
        var entry = new ImportDraftEntry(Person(string.Empty, "No Handle"));

        Assert.Equal("Person · needs a username", entry.Subtitle);
        Assert.False(entry.IsReady);
    }

    [Fact]
    public void TheSelectionLineCountsPeopleAndGroups()
    {
        var draft = new ImportDraft([Person("sari"), Person("budi"), Group("Keluarga")]);

        Assert.Equal("2 people and 1 group selected of 3", draft.DescribeSelection());

        draft.IncludeNone();

        Assert.Equal("none selected of 3", draft.DescribeSelection());
    }

    [Fact]
    public void OneOfEachIsCountedInTheSingular()
    {
        var draft = new ImportDraft([Person("sari"), Group("Keluarga")]);

        Assert.Equal("1 person and 1 group selected of 2", draft.DescribeSelection());
    }

    [Fact]
    public void AnEmptyScanDescribesNothing()
    {
        Assert.Equal(string.Empty, new ImportDraft([]).DescribeSelection());
        Assert.Equal(string.Empty, new ImportDraft(null).DescribeSelection());
    }

    // ─── The rules a correction has to follow ────────────────────────────────

    [Theory]
    [InlineData("sari", "sari")]
    [InlineData("  sari  ", "sari")]
    [InlineData("@sari", "sari")]
    [InlineData(" @sari ", "sari")]
    [InlineData("sari.rahma_1", "sari.rahma_1")]
    public void AHandleIsAcceptedAndTidied(string typed, string expected)
    {
        var entry = Person(string.Empty);

        Assert.True(ContactEditRules.TrySetUsername(entry, typed, out var error), error);
        Assert.Equal(expected, entry.Username);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@")]
    [InlineData("sa ri")]
    [InlineData("sa@ri")]
    [InlineData("sa/ri")]
    public void AHandleThatCouldNeverBeMatchedIsRefused(string typed)
    {
        var entry = Person("sari");

        Assert.False(ContactEditRules.TrySetUsername(entry, typed, out var error));
        Assert.NotNull(error);

        // Refused means unchanged: a rejected edit must not half-apply.
        Assert.Equal("sari", entry.Username);
    }

    [Fact]
    public void AnOverlongHandleIsRefused()
    {
        var entry = Person("sari");
        var limit = ContactEditRules.MaxUsernameLength;

        Assert.False(ContactEditRules.TrySetUsername(entry, new string('a', limit + 1), out _));
        Assert.True(ContactEditRules.TrySetUsername(entry, new string('a', limit), out _));
    }

    [Fact]
    public void ANameIsTidiedAndRefusedWhenEmpty()
    {
        var entry = Person("sari", "Sari");

        Assert.True(ContactEditRules.TrySetDisplayName(entry, "  Sari  ", out _));
        Assert.Equal("Sari", entry.DisplayName);

        Assert.False(ContactEditRules.TrySetDisplayName(entry, "   ", out var error));
        Assert.NotNull(error);

        // Refused means unchanged, the same as a handle.
        Assert.Equal("Sari", entry.DisplayName);
    }

    [Fact]
    public void ANameThatArrivesWithLineBreaksIsFlattened()
    {
        var entry = Group("Old");

        Assert.True(ContactEditRules.TrySetDisplayName(entry, "Keluarga\nBesar", out _));

        Assert.Equal("Keluarga Besar", entry.DisplayName);
    }

    [Fact]
    public void AnOverlongNameIsRefused()
    {
        var entry = Group("Old");
        var limit = ContactEditRules.MaxNameLength;

        Assert.False(ContactEditRules.TrySetDisplayName(entry, new string('a', limit + 1), out _));
        Assert.True(ContactEditRules.TrySetDisplayName(entry, new string('a', limit), out _));
    }

    [Fact]
    public void AGroupWithoutANameIsRefusedWithItsOwnMessage()
    {
        var entry = Group("Keluarga");

        Assert.False(ContactEditRules.TrySetDisplayName(entry, "  ", out var error));

        // A group is matched by the name in its header, so the message has to say what is missing.
        Assert.Contains("chat header", error);
    }

    [Fact]
    public void CorrectingNothingIsRefusedRatherThanCrashing()
    {
        Assert.False(ContactEditRules.TrySetUsername(null, "sari", out _));
        Assert.False(ContactEditRules.TrySetDisplayName(null, "Sari", out _));
        ContactEditRules.SetGroup(null, true);
    }
}
