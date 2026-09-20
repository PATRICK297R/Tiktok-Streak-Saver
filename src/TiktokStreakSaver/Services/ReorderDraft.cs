namespace TiktokStreakSaver.Services;

using TiktokStreakSaver.Models;

/// <summary>
/// An order that has been arranged but not saved yet.
/// <para>
/// Dragging a card around the list changes what is on screen, and it would be easy to make that change
/// stick as it happens. It deliberately does not: the arrangement is held here, in copies of the entries,
/// and only reaches storage when the user says so. Cancelling throws the copies away, so nothing that was
/// cancelled can have been saved - not even partly.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public sealed class ReorderDraft
{
    private readonly List<FriendConfig> _entries;

    /// <summary>The order this draft started from, used to tell a real arrangement from a tap on Save.</summary>
    private readonly List<string> _startingOrder;

    private ReorderDraft(List<FriendConfig> entries)
    {
        _entries = entries;
        _startingOrder = entries.Select(e => e.Id).ToList();
    }

    /// <summary>
    /// Starts a draft from what is stored. The entries are copies, so nothing in here can reach the objects
    /// the rest of the app is holding - and an edit made elsewhere cannot appear in a draft mid-arrangement.
    /// </summary>
    public static ReorderDraft Begin(IEnumerable<FriendConfig>? stored)
    {
        var entries = stored is null
            ? new List<FriendConfig>()
            : stored.Where(e => e is not null).Select(e => e.Clone()).ToList();

        return new ReorderDraft(entries);
    }

    /// <summary>The arranged order so far, which is what the list is drawn from while the mode is on.</summary>
    public IReadOnlyList<FriendConfig> Entries => _entries;

    /// <summary>
    /// Whether anything has actually moved. Used to keep Save switched off until there is something to
    /// save, rather than letting it write an order identical to the one already stored.
    /// </summary>
    public bool HasChanges => !_entries.Select(e => e.Id).SequenceEqual(_startingOrder);

    /// <summary>
    /// Moves one entry to another's place within its own section. Staged only: storage is untouched.
    /// </summary>
    public bool Move(string? draggedId, string? targetId) =>
        FriendOrder.TryMoveWithinSection(_entries, draggedId, targetId);

    /// <summary>
    /// The order to save, as copies again, so that a draft cannot be altered through the list that was
    /// handed to storage.
    /// </summary>
    public List<FriendConfig> ToSave() => _entries.Select(e => e.Clone()).ToList();
}
