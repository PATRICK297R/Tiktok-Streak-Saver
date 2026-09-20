using System.Reflection;
using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// Arranging the send order is staged: it changes what is on screen and nothing else until it is saved.
/// These tests pin that, because the failure mode is invisible - an order that was cancelled but saved
/// anyway looks exactly like an order that was never touched.
/// </summary>
public class ReorderDraftTests
{
    private static FriendConfig Entry(string id, bool enabled = true, bool isGroup = false) =>
        new() { Id = id, Username = id, DisplayName = id, IsEnabled = enabled, IsGroup = isGroup };

    private static string[] Ids(IEnumerable<FriendConfig> entries) => entries.Select(e => e.Id).ToArray();

    [Fact]
    public void BeginCopiesTheEntriesSoMovingThemCannotReachTheOriginals()
    {
        var stored = new List<FriendConfig> { Entry("a"), Entry("b") };

        var draft = ReorderDraft.Begin(stored);
        Assert.True(draft.Move("b", "a"));

        // The draft moved; the objects the rest of the app is holding did not.
        Assert.Equal(new[] { "b", "a" }, Ids(draft.Entries));
        Assert.Equal(new[] { "a", "b" }, Ids(stored));
    }

    [Fact]
    public void AnEditMadeElsewhereDoesNotAppearInAnOpenDraft()
    {
        var stored = new List<FriendConfig> { Entry("a") };
        var draft = ReorderDraft.Begin(stored);

        stored[0].DisplayName = "changed while arranging";

        // The draft is a snapshot, so what is on screen cannot shift under the user mid-arrangement.
        Assert.NotEqual("changed while arranging", draft.Entries[0].DisplayName);
    }

    [Fact]
    public void AStagedMoveNeverReachesTheStoredList()
    {
        var stored = new List<FriendConfig> { Entry("a"), Entry("b"), Entry("c") };

        var draft = ReorderDraft.Begin(stored);
        Assert.True(draft.Move("c", "a"));

        Assert.Equal(new[] { "a", "b", "c" }, Ids(stored));
    }

    [Fact]
    public void NothingHasChangedUntilSomethingMoves()
    {
        var draft = ReorderDraft.Begin(new List<FriendConfig> { Entry("a"), Entry("b") });

        // Save is kept switched off on this, so it cannot write an order identical to the stored one.
        Assert.False(draft.HasChanges);
    }

    [Fact]
    public void MovingAnEntryMarksTheDraftAsChanged()
    {
        var draft = ReorderDraft.Begin(new List<FriendConfig> { Entry("a"), Entry("b") });

        Assert.True(draft.Move("b", "a"));
        Assert.True(draft.HasChanges);
    }

    [Fact]
    public void MovingAnEntryBackLeavesNothingToSave()
    {
        var draft = ReorderDraft.Begin(new List<FriendConfig> { Entry("a"), Entry("b"), Entry("c") });

        Assert.True(draft.Move("c", "a"));
        Assert.True(draft.HasChanges);

        Assert.True(draft.Move("c", "b"));
        Assert.Equal(new[] { "a", "b", "c" }, Ids(draft.Entries));
        Assert.False(draft.HasChanges);
    }

    [Fact]
    public void ARefusedMoveDoesNotMarkTheDraftAsChanged()
    {
        var draft = ReorderDraft.Begin(new List<FriendConfig> { Entry("a"), Entry("b") });

        Assert.False(draft.Move("a", "a"));
        Assert.False(draft.Move("missing", "a"));

        Assert.False(draft.HasChanges);
    }

    [Fact]
    public void ToSaveHandsOverTheArrangedOrder()
    {
        var draft = ReorderDraft.Begin(new List<FriendConfig> { Entry("a"), Entry("b"), Entry("c") });
        Assert.True(draft.Move("c", "a"));

        Assert.Equal(new[] { "c", "a", "b" }, Ids(draft.ToSave()));
    }

    [Fact]
    public void ToSaveHandsOverCopies()
    {
        var draft = ReorderDraft.Begin(new List<FriendConfig> { Entry("a") });

        var saved = draft.ToSave();
        saved[0].DisplayName = "edited after saving";

        // Storage is handed copies, so it cannot be altered through a draft that is still open.
        Assert.NotEqual("edited after saving", draft.Entries[0].DisplayName);
    }

    [Fact]
    public void AGroupCannotBeDroppedOnAFriend()
    {
        var draft = ReorderDraft.Begin(new List<FriendConfig>
        {
            Entry("f"), Entry("g", isGroup: true)
        });

        Assert.False(draft.Move("f", "g"));
        Assert.False(draft.HasChanges);
    }

    [Fact]
    public void ADraftOfNothingIsEmpty()
    {
        Assert.Empty(ReorderDraft.Begin(null).Entries);
        Assert.False(ReorderDraft.Begin(null).HasChanges);
        Assert.Empty(ReorderDraft.Begin(new List<FriendConfig>()).ToSave());
    }

    // ─── The clone the draft is built from ───────────────────────────────────

    private static IEnumerable<PropertyInfo> SettableProperties() =>
        typeof(FriendConfig).GetProperties().Where(p => p is { CanRead: true, CanWrite: true });

    /// <summary>
    /// Gives every settable property a value different from its default, so a property the clone forgets
    /// shows up as a difference rather than as two defaults that happen to match.
    /// </summary>
    private static void FillWithNonDefaultValues(FriendConfig entry)
    {
        foreach (var property in SettableProperties())
        {
            var current = property.GetValue(entry);
            var type = property.PropertyType;

            object? value;

            if (type == typeof(string))
                value = "sample-value";
            else if (type == typeof(bool))
                value = !(bool)(current ?? false);
            else if (type == typeof(int))
                value = (int)(current ?? 0) + 7;
            else if (type == typeof(DateTime?))
                value = new DateTime(2026, 3, 4, 5, 6, 7);
            else
                throw new NotSupportedException(
                    $"FriendConfig gained a property of type {type.Name} ({property.Name}). Add a sample value "
                    + "for it here, so the clone test still proves the property is copied.");

            property.SetValue(entry, value);
        }
    }

    [Fact]
    public void CloneCopiesEverySettableProperty()
    {
        var original = new FriendConfig();
        FillWithNonDefaultValues(original);

        var copy = original.Clone();

        Assert.NotSame(original, copy);

        foreach (var property in SettableProperties())
        {
            Assert.Equal(property.GetValue(original), property.GetValue(copy));
        }
    }

    [Fact]
    public void CloneOfAnEntryWithNoCustomMessageKeepsItEmpty()
    {
        var original = new FriendConfig { Id = "a", Username = "a", DisplayName = "a" };

        var copy = original.Clone();

        Assert.Null(copy.CustomMessage);
        Assert.False(copy.HasCustomMessage);
    }

    [Fact]
    public void TheDraftCarriesEverythingARowCanHold()
    {
        // Everything a row can hold has to make it into the draft, because the draft is what gets saved.
        var stored = new List<FriendConfig>
        {
            new()
            {
                Id = "a", Username = "user", DisplayName = "Name", IsEnabled = false,
                IsGroup = true, CustomMessage = "hi {name}",
                LastMessageSent = new DateTime(2026, 3, 4, 5, 6, 7), SuccessCount = 3, FailureCount = 4
            }
        };

        var saved = ReorderDraft.Begin(stored).ToSave();

        Assert.Equal("user", saved[0].Username);
        Assert.Equal("Name", saved[0].DisplayName);
        Assert.False(saved[0].IsEnabled);
        Assert.True(saved[0].IsGroup);
        Assert.Equal("hi {name}", saved[0].CustomMessage);
        Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7), saved[0].LastMessageSent);
        Assert.Equal(3, saved[0].SuccessCount);
        Assert.Equal(4, saved[0].FailureCount);
    }
}
