namespace TiktokStreakSaver.Services;

using TiktokStreakSaver.Models;

/// <summary>
/// The rules a correction has to follow before it can be saved.
/// <para>
/// The scanner reads a chat's name, its handle and whether it looked like a group, and it can be wrong
/// about any of the three: a group whose name is a single word reads as a person, and a person whose
/// display name is not their handle can read as a group. The review list is where that is put right, so
/// the corrections are checked here rather than being trusted - an entry that can never be matched is
/// worse than one the user removed, because it fails silently on every run.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class ContactEditRules
{
    /// <summary>Longest handle accepted. TikTok's own limit is 24, with room to spare.</summary>
    public const int MaxUsernameLength = 40;

    /// <summary>Longest chat name accepted. A name this long is a paste, not a chat.</summary>
    public const int MaxNameLength = 80;

    /// <summary>
    /// Corrects the handle of a person.
    /// <para>
    /// The same shape the scanner insists on, so a corrected entry and a scanned one cannot come to
    /// mean two different things: the handle is what the app rebuilds the chat's address from, so a
    /// space, an <c>@</c> or a slash in it could never find the chat again. A leading <c>@</c> is
    /// stripped rather than refused, because typing it is natural and it is not part of the handle.
    /// </para>
    /// </summary>
    public static bool TrySetUsername(FriendConfig? entry, string? raw, out string? error)
    {
        error = null;
        if (entry is null)
        {
            error = "There is nothing to correct.";
            return false;
        }

        var username = (raw ?? string.Empty).Trim().TrimStart('@').Trim();

        if (username.Length == 0)
        {
            error = "Enter the TikTok username, without the @.";
            return false;
        }

        if (username.Length > MaxUsernameLength)
        {
            error = $"A username is at most {MaxUsernameLength} characters.";
            return false;
        }

        if (username.Any(c => char.IsWhiteSpace(c) || c == '@' || c == '/'))
        {
            error = "A username cannot contain spaces, @ or /.";
            return false;
        }

        entry.Username = username;
        return true;
    }

    /// <summary>
    /// Corrects the chat name, which is what a group is matched by and what a person is listed under.
    /// </summary>
    public static bool TrySetDisplayName(FriendConfig? entry, string? raw, out string? error)
    {
        error = null;
        if (entry is null)
        {
            error = "There is nothing to correct.";
            return false;
        }

        var name = (raw ?? string.Empty).Replace('\n', ' ').Trim();

        if (name.Length == 0)
        {
            error = entry.IsGroup
                ? "A group needs the name shown in its chat header."
                : "Enter the name to show for this contact.";
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            error = $"A name is at most {MaxNameLength} characters.";
            return false;
        }

        entry.DisplayName = name;
        return true;
    }

    /// <summary>
    /// Changes what an entry is matched by.
    /// <para>
    /// A person is looked up by handle and a group by chat name, so the two fields swap roles here. What
    /// each one needs is not enforced - an entry switched to a person before its handle is typed is
    /// simply not ready yet, and the list says so - because refusing the change would make the order the
    /// user does things in matter.
    /// </para>
    /// </summary>
    public static void SetGroup(FriendConfig? entry, bool isGroup)
    {
        if (entry is not null)
            entry.IsGroup = isGroup;
    }
}

/// <summary>
/// One scanned chat in the review list, with what the user has decided about it.
/// <para>
/// The scanner is a good guess and not an authority, so nothing is saved until the list has been seen.
/// Holding the decision next to the entry is what makes "add only these" and "put this one right" the
/// same screen, and the corrections are written straight onto the entry that will be saved rather than
/// onto a copy - a review step whose edits are dropped is worse than no review step.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public sealed class ImportDraftEntry
{
    public ImportDraftEntry(FriendConfig entry)
    {
        Entry = entry;
        Include = true;
    }

    /// <summary>The entry itself, as it will be saved.</summary>
    public FriendConfig Entry { get; }

    /// <summary>Whether this is one of the entries to add.</summary>
    public bool Include { get; set; }

    /// <summary>The row's heading: the chat name, or the handle when there is no name to show.</summary>
    public string Title
    {
        get
        {
            var name = (Entry.DisplayName ?? string.Empty).Trim();
            return name.Length > 0 ? name : (Entry.Username ?? string.Empty).Trim();
        }
    }

    /// <summary>
    /// The row's second line, which is also where an entry that cannot be saved says so. Saying it here
    /// rather than in a message after the fact is the point of reviewing: the row that is wrong is the
    /// row that explains itself.
    /// </summary>
    public string Subtitle
    {
        get
        {
            if (Entry.IsGroup)
                return "Group · matched by its chat name";

            var username = (Entry.Username ?? string.Empty).Trim();
            return username.Length > 0 ? "@" + username : "Person · needs a username";
        }
    }

    /// <summary>Whether this entry could be saved as it stands.</summary>
    public bool IsReady => BackupMergePolicy.IsUsable(Entry);
}

/// <summary>
/// The scanned chats awaiting confirmation: which of them to add, and in what form.
/// <para>
/// Built because the scan adds everything it finds, and the scan can be wrong. A group read as a person
/// would be looked up by a member's handle and open that member's chat instead of the group, so the
/// chance to catch it before it is saved is worth a screen.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public sealed class ImportDraft
{
    private readonly List<ImportDraftEntry> _entries;

    public ImportDraft(IEnumerable<FriendConfig>? scanned)
    {
        _entries = (scanned ?? [])
            .Where(entry => entry is not null)
            .Select(entry => new ImportDraftEntry(entry))
            .ToList();
    }

    /// <summary>Every entry, in the order the scan found them - which is the order they are added in.</summary>
    public IReadOnlyList<ImportDraftEntry> Entries => _entries;

    public int Count => _entries.Count;

    /// <summary>How many entries are ticked, whether or not they are ready.</summary>
    public int IncludedCount => _entries.Count(entry => entry.Include);

    /// <summary>Ticked entries that are missing what they need, so the reason can be given up front.</summary>
    public int BlockedCount => _entries.Count(entry => entry.Include && !entry.IsReady);

    public void IncludeAll()
    {
        foreach (var entry in _entries) entry.Include = true;
    }

    public void IncludeNone()
    {
        foreach (var entry in _entries) entry.Include = false;
    }

    /// <summary>
    /// What to save: the ticked entries that are ready, in scan order. An entry that is not ready is left
    /// out rather than saved broken, and the caller is expected to have said so before this is reached.
    /// </summary>
    public List<FriendConfig> ToAdd() =>
        _entries.Where(entry => entry.Include && entry.IsReady).Select(entry => entry.Entry).ToList();

    /// <summary>The selection as a line of text, for the summary above the list.</summary>
    public string DescribeSelection()
    {
        if (_entries.Count == 0)
            return string.Empty;

        var people = _entries.Count(entry => entry.Include && !entry.Entry.IsGroup);
        var groups = _entries.Count(entry => entry.Include && entry.Entry.IsGroup);

        var parts = new List<string>();
        if (people > 0) parts.Add(people == 1 ? "1 person" : $"{people} people");
        if (groups > 0) parts.Add(groups == 1 ? "1 group" : $"{groups} groups");

        var selected = parts.Count > 0 ? string.Join(" and ", parts) : "none";
        return $"{selected} selected of {_entries.Count}";
    }

    /// <summary>
    /// Why the ticked entries cannot all be saved, or null when they can.
    /// <para>
    /// Named rather than counted, because the fix is per row: "1 entry needs a username" sends the user
    /// looking, while naming it sends them to the row.
    /// </para>
    /// </summary>
    public string? DescribeBlocked()
    {
        var blocked = _entries.Where(entry => entry.Include && !entry.IsReady).ToList();
        if (blocked.Count == 0)
            return null;

        var names = blocked.Select(entry => entry.Title.Length > 0 ? entry.Title : "(unnamed)").ToList();
        var which = names.Count == 1 ? names[0] : string.Join(", ", names.Take(3))
            + (names.Count > 3 ? $" and {names.Count - 3} more" : string.Empty);

        return blocked.Count == 1
            ? $"{which} needs a username before it can be added."
            : $"{which} need a username before they can be added.";
    }
}

