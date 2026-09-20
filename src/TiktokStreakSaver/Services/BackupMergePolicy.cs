namespace TiktokStreakSaver.Services;

using TiktokStreakSaver.Models;

/// <summary>
/// What a restore would change, so the page can describe it before it happens.
/// </summary>
/// <param name="Friends">The list to save.</param>
/// <param name="Added">Entries from the backup that are new to this device.</param>
/// <param name="Skipped">Entries from the backup already present, matched by id or by identity.</param>
public readonly record struct BackupMergeResult(List<FriendConfig> Friends, int Added, int Skipped);

/// <summary>
/// Decides what a restore does to the list already on the device.
/// <para>
/// Restoring is a <em>merge</em> by default rather than a replace, because the two situations that
/// lead here pull in opposite directions: after a reinstall the list is empty and merging is the same
/// as replacing, while a user who just wants a lost entry back would have their newer edits wiped by
/// a replace. Merging is the only rule that is safe in both, and it never loses what is on the device
/// now.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class BackupMergePolicy
{
    /// <summary>
    /// Adds whatever the backup has and the device does not.
    /// <para>
    /// The existing list keeps its order, because that order is the send queue. Restored entries are
    /// appended in the order the backup holds them, at the end - they are the ones the user has not
    /// seen in this list yet.
    /// </para>
    /// </summary>
    public static BackupMergeResult Merge(IEnumerable<FriendConfig>? current, IEnumerable<FriendConfig>? incoming)
    {
        var merged = new List<FriendConfig>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in current ?? [])
        {
            if (entry is null || !IsUsable(entry))
                continue;

            merged.Add(entry);

            foreach (var key in IdentityKeys(entry))
                known.Add(key);
        }

        int added = 0;
        int skipped = 0;

        foreach (var entry in incoming ?? [])
        {
            if (entry is null || !IsUsable(entry))
                continue;

            var keys = IdentityKeys(entry).ToList();

            // Matched on any key: the same id, or the same chat under a different id.
            if (keys.Any(known.Contains))
            {
                skipped++;
                continue;
            }

            foreach (var key in keys)
                known.Add(key);

            merged.Add(entry);
            added++;
        }

        return new BackupMergeResult(merged, added, skipped);
    }

    /// <summary>
    /// The backup's list on its own, for the explicit "replace everything" choice. Unusable entries
    /// are dropped and duplicates collapsed, so a hand-edited file cannot leave the app with two
    /// entries for one chat.
    /// </summary>
    public static List<FriendConfig> Replace(IEnumerable<FriendConfig>? incoming)
    {
        var result = new List<FriendConfig>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in incoming ?? [])
        {
            if (entry is null || !IsUsable(entry))
                continue;

            var keys = IdentityKeys(entry).ToList();
            if (keys.Any(seen.Contains))
                continue;

            foreach (var key in keys)
                seen.Add(key);

            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Every key an entry can be recognised by.
    /// <para>
    /// More than one, because a single key is not enough. The id alone misses a hand-edited or
    /// re-imported file whose entries carry fresh ids but name the same chat; the name alone misses
    /// an entry whose username the user corrected after the backup was written, where only the id
    /// still ties the two together. Checking both means "already here" is decided by whichever
    /// evidence survives.
    /// </para>
    /// </summary>
    public static IEnumerable<string> IdentityKeys(FriendConfig entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Id))
            yield return "id:" + entry.Id.Trim();

        if (entry.IsGroup)
        {
            var groupName = (entry.DisplayName ?? string.Empty).Trim();
            if (groupName.Length > 0)
                yield return "group:" + groupName;
        }
        else
        {
            var username = (entry.Username ?? string.Empty).Trim();
            if (username.Length > 0)
                yield return "user:" + username;
        }
    }

    /// <summary>
    /// Whether an entry names anything at all. A friend needs a username; a group is matched by its
    /// chat header, so it needs a display name.
    /// </summary>
    public static bool IsUsable(FriendConfig? entry) =>
        entry is not null &&
        (entry.IsGroup
            ? !string.IsNullOrWhiteSpace(entry.DisplayName)
            : !string.IsNullOrWhiteSpace(entry.Username));
}
