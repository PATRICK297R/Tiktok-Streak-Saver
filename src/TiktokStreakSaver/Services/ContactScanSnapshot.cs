namespace TiktokStreakSaver.Services;

using System.Text.Json.Serialization;
using TiktokStreakSaver.Models;

/// <summary>
/// How far a chat import has got.
/// <para>
/// The scan no longer belongs to the page that started it: it runs in a foreground service so the app
/// can be closed while it works. That means the page and the service need a shared, nameable idea of
/// "where are we", which is what this is.
/// </para>
/// </summary>
public enum ContactScanPhase
{
    /// <summary>Nothing has been asked for, or the last results were cleared.</summary>
    Idle,

    /// <summary>Asked for, but the page has not loaded or the scanner has not reported yet.</summary>
    Starting,

    /// <summary>The scanner is reporting. Conversations are being read.</summary>
    Running,

    /// <summary>Finished on its own. <see cref="ContactScanSnapshot.Items"/> holds what was read.</summary>
    Finished,

    /// <summary>Stopped by the user. Whatever was read first is still in the snapshot.</summary>
    Cancelled,

    /// <summary>Could not run, or stopped in a way that is not a normal finish. <see cref="ContactScanSnapshot.Message"/> says why.</summary>
    Failed
}

/// <summary>
/// The state of a chat import, as both the service and the page see it.
/// <para>
/// The scanner's own report (<see cref="ContactScanDto"/>) is what arrives from the page; this is what
/// survives it. Kept separate on purpose: the report is per-page-load and is replaced whenever the
/// scanner restarts, while this accumulates across restarts and outlives the process.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public sealed class ContactScanSnapshot
{
    [JsonPropertyName("phase")] public ContactScanPhase Phase { get; set; } = ContactScanPhase.Idle;

    /// <summary>Why the scan failed, or what it was told to stop for. Empty while it is working.</summary>
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;

    /// <summary>How many conversations the scanner has opened, which is what the progress shows.</summary>
    [JsonPropertyName("attempts")] public int Attempts { get; set; }

    /// <summary>Every conversation read so far, across every report.</summary>
    [JsonPropertyName("items")] public List<ContactScanItemDto> Items { get; set; } = new();

    /// <summary>The scanner's own progress notes, newest last.</summary>
    [JsonPropertyName("log")] public List<string> Log { get; set; } = new();

    /// <summary>The page the scanner ended up on, which is how "found nothing" and "wrong page" are told apart.</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;

    [JsonPropertyName("ua")] public string UserAgent { get; set; } = string.Empty;

    [JsonPropertyName("startedAt")] public DateTime StartedAt { get; set; }

    /// <summary>
    /// When this was last written. Used to tell a scan that is still working from one whose process was
    /// killed: a snapshot that says it is running but has not been touched for minutes is not running.
    /// </summary>
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }

    /// <summary>True while the service is still expected to make progress.</summary>
    [JsonIgnore] public bool IsRunning => Phase is ContactScanPhase.Starting or ContactScanPhase.Running;

    /// <summary>True once the scan has stopped for any reason, which is when there is something to show.</summary>
    [JsonIgnore] public bool IsStopped => Phase is ContactScanPhase.Finished or ContactScanPhase.Cancelled or ContactScanPhase.Failed;

    /// <summary>
    /// A snapshot that claims to be running but has not been written for <paramref name="timeout"/> is
    /// treated as dead.
    /// <para>
    /// Only reachable after the process was killed: the service writes at least every couple of seconds
    /// while it works, and always when it stops. Without this the page would wait forever on a scan that
    /// no longer exists, because the only record of it says "running".
    /// </para>
    /// </summary>
    public static bool IsAbandoned(ContactScanSnapshot? snapshot, DateTime nowUtc, TimeSpan timeout)
        => snapshot is not null && snapshot.IsRunning && nowUtc - snapshot.UpdatedAt > timeout;

    /// <summary>
    /// What the page and the notification say while the scan works. Worded the same as the version that
    /// ran inside the page, because it is describing the same thing.
    /// </summary>
    public static string ProgressText(ContactScanSnapshot? snapshot)
    {
        if (snapshot is null)
            return "Reading your chats…";

        return snapshot.Phase switch
        {
            ContactScanPhase.Starting => "Opening your inbox…",
            ContactScanPhase.Running => snapshot.Items.Count > 0
                ? $"Reading your chats… {snapshot.Items.Count} found, {snapshot.Attempts} checked"
                : snapshot.Attempts > 0
                    ? $"Reading your chats… opening chat {snapshot.Attempts}"
                    : "Reading your chats…",
            ContactScanPhase.Finished => $"Read {snapshot.Items.Count} {(snapshot.Items.Count == 1 ? "conversation" : "conversations")}.",
            ContactScanPhase.Cancelled => $"Stopped after {snapshot.Items.Count} {(snapshot.Items.Count == 1 ? "conversation" : "conversations")}.",
            ContactScanPhase.Failed => snapshot.Message.Length > 0 ? snapshot.Message : "The scan could not run.",
            _ => "Reading your chats…"
        };
    }

    /// <summary>
    /// One line for the notification that stays after the scan ends.
    /// <para>
    /// It says what to do next, because this notification is the only thing the user sees when the scan
    /// finished while the app was closed - and a notification that only reports a number leaves them
    /// with no idea that the results are waiting for them.
    /// </para>
    /// </summary>
    public static string SummaryText(ContactScanSnapshot? snapshot)
    {
        if (snapshot is null)
            return "Nothing was read.";

        return snapshot.Phase switch
        {
            ContactScanPhase.Finished => snapshot.Items.Count > 0
                ? $"Read {snapshot.Items.Count} {(snapshot.Items.Count == 1 ? "chat" : "chats")} — tap to review and add them."
                : "No conversations were found in your inbox.",
            ContactScanPhase.Cancelled => snapshot.Items.Count > 0
                ? $"Stopped. {snapshot.Items.Count} {(snapshot.Items.Count == 1 ? "chat was" : "chats were")} read — tap to review them."
                : "Stopped before anything could be read.",
            ContactScanPhase.Failed => snapshot.Message.Length > 0
                ? $"Could not read your chats: {snapshot.Message}"
                : "Could not read your chats.",
            _ => ProgressText(snapshot)
        };
    }

    /// <summary>
    /// Chats in the shape the scanner reports them.
    /// <para>
    /// The service keeps what it has read as app entries, because that is what gets saved; the snapshot
    /// stores them as the scanner reported them, so the reader that understands a live report understands
    /// a stored one too, and the page can go straight from a finished scan to the list of chats to review.
    /// </para>
    /// </summary>
    public static List<ContactScanItemDto> ItemsFrom(IEnumerable<FriendConfig>? friends)
    {
        var items = new List<ContactScanItemDto>();

        foreach (var friend in friends ?? [])
        {
            if (friend is null)
                continue;

            items.Add(new ContactScanItemDto
            {
                Username = friend.Username ?? string.Empty,
                DisplayName = friend.DisplayName ?? string.Empty,
                IsGroup = friend.IsGroup
            });
        }

        return items;
    }

    /// <summary>
    /// The same state in the shape the scanner's own reader already understands, so the messages written
    /// for an empty scan can be reused rather than written a second time.
    /// </summary>
    public ContactScanDto ToScanDto() => new()
    {
        Done = Phase == ContactScanPhase.Finished,
        Attempts = Attempts,
        Items = Items,
        Log = Log,
        Url = Url,
        UserAgent = UserAgent
    };
}
