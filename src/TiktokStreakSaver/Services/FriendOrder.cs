namespace TiktokStreakSaver.Services;

using TiktokStreakSaver.Models;

/// <summary>
/// Reordering rules for the streak send queue.
/// <para>
/// The order entries are messaged in is simply their order in the saved friends list: the Android
/// runner walks an index through the enabled entries one at a time, and the iOS runner reads the same
/// list. There is deliberately <em>no</em> separate "sort order" field, because a field and an array
/// position would be two sources of truth for one fact - the same kind of drift that made the
/// Activity page disagree with the Dashboard about the last run.
/// </para>
/// <para>
/// Moves skip over disabled entries so that every move changes what the user sees. Swapping with a
/// disabled neighbour would reorder the underlying list without changing the queue, and the button
/// would look broken.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class FriendOrder
{
    /// <summary>
    /// The entries that will actually be messaged, in the order they will be messaged. This is what
    /// the "Send order" list shows, and what the runner walks.
    /// </summary>
    public static List<FriendConfig> SendQueue(IEnumerable<FriendConfig>? all) =>
        all is null
            ? new List<FriendConfig>()
            : all.Where(f => f is not null && f.IsEnabled).ToList();

    /// <summary>How many enabled entries there are, without building a list.</summary>
    public static int CountInQueue(IEnumerable<FriendConfig>? all) =>
        all?.Count(f => f is not null && f.IsEnabled) ?? 0;

    /// <summary>
    /// Moves an entry one place earlier in the send queue, past the nearest enabled entry above it.
    /// Returns false when it is already first (or not found, or disabled), so the caller can leave the
    /// button disabled rather than silently doing nothing.
    /// </summary>
    public static bool TryMoveUp(List<FriendConfig>? all, string? id) =>
        TryMove(all, id, forward: false);

    /// <summary>
    /// Moves an entry one place later in the send queue, past the nearest enabled entry below it.
    /// Returns false when it is already last (or not found, or disabled).
    /// </summary>
    public static bool TryMoveDown(List<FriendConfig>? all, string? id) =>
        TryMove(all, id, forward: true);

    /// <summary>
    /// Moves an entry so that it sits where another one sits now, which is what dropping a row onto
    /// another row means: the dragged entry takes the target's place and everything between them shifts
    /// by one. Returns false when nothing would change - the same entry, an unknown id, or a disabled one.
    /// <para>
    /// Expressed against the queue rather than the raw list, so a drag and an arrow press cannot disagree
    /// about what a move is: both are moves within the send queue.
    /// </para>
    /// </summary>
    public static bool TryMoveTo(List<FriendConfig>? all, string? id, string? targetId)
    {
        if (all is null || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(targetId) || id == targetId)
            return false;

        var queue = SendQueue(all);

        // Disabled entries are not in the queue, so they are neither on screen to be dragged nor on
        // screen to be dropped on. Refusing is what keeps the move honest.
        var from = queue.FindIndex(f => f.Id == id);
        var to = queue.FindIndex(f => f.Id == targetId);

        if (from < 0 || to < 0 || from == to)
            return false;

        var dragged = queue[from];
        queue.RemoveAt(from);
        queue.Insert(to, dragged);

        ApplyQueueOrder(all, queue);
        return true;
    }

    /// <summary>
    /// Moves an entry so that it sits where another entry sits, which is what dragging one card onto
    /// another means. The dragged entry takes the target's place and everything between them shifts by one.
    /// <para>
    /// Works across the whole saved list, including between a friend and a group. There is only one saved
    /// sequence - the Friends and Groups sections are two filtered views of it - so the position of a group
    /// relative to a friend is a real fact about the send order. Restricting a move to its own kind used to
    /// hide that: the arrangement was editable within each section but the interleaving between them was
    /// frozen, and nothing on screen said so.
    /// </para>
    /// <para>
    /// Disabled entries take part, unlike in <see cref="TryMoveTo"/>. The arranging list shows them with
    /// their switch off, so a row the user can see and drag has to move when they drag it; a handle that
    /// did nothing would read as a broken control.
    /// </para>
    /// </summary>
    public static bool TryMoveWithinList(List<FriendConfig>? all, string? draggedId, string? targetId)
    {
        if (all is null || string.IsNullOrEmpty(draggedId) || string.IsNullOrEmpty(targetId) || draggedId == targetId)
            return false;

        var from = all.FindIndex(f => f is not null && f.Id == draggedId);
        var to = all.FindIndex(f => f is not null && f.Id == targetId);

        if (from < 0 || to < 0 || from == to)
            return false;

        var dragged = all[from];
        all.RemoveAt(from);
        all.Insert(to, dragged);

        return true;
    }

    /// <summary>
    /// Writes a queue order back onto the saved list.
    /// <para>
    /// Only the enabled slots are refilled, in the new order, so disabled entries keep the slots they are
    /// in. They are not part of the queue, and reordering the queue must not shuffle them around.
    /// </para>
    /// </summary>
    private static void ApplyQueueOrder(List<FriendConfig> all, List<FriendConfig> queue)
    {
        var next = 0;

        for (var i = 0; i < all.Count && next < queue.Count; i++)
        {
            if (!all[i].IsEnabled)
                continue;

            all[i] = queue[next++];
        }
    }

    private static bool TryMove(List<FriendConfig>? all, string? id, bool forward)
    {
        if (all is null || string.IsNullOrEmpty(id))
            return false;

        var from = all.FindIndex(f => f is not null && f.Id == id);

        // Only enabled entries are in the queue, so only those can be moved.
        if (from < 0 || !all[from].IsEnabled)
            return false;

        var step = forward ? 1 : -1;

        for (var target = from + step; target >= 0 && target < all.Count; target += step)
        {
            if (!all[target].IsEnabled)
                continue;

            (all[target], all[from]) = (all[from], all[target]);
            return true;
        }

        return false;
    }
}
