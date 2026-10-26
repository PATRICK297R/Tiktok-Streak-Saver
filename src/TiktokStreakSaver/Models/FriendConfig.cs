namespace TiktokStreakSaver.Models;

/// <summary>
/// Represents a friend configuration for sending streak messages
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class FriendConfig
{
    /// <summary>
    /// Unique identifier for this friend entry
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// TikTok username of the friend (without @)
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Display name for easier identification
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this friend is enabled for streak messages
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Last time a streak message was successfully sent to this friend
    /// </summary>
    public DateTime? LastMessageSent { get; set; }

    /// <summary>
    /// Number of successful messages sent
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of failed attempts
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// Consecutive terminal send failures since the last confirmed success.
    /// <para>
    /// Separate from <see cref="FailureCount"/>, which only ever grows: a chat that failed twice last
    /// week and then worked fine is not unhealthy today. This resets to zero on every success, which is
    /// what lets the friends list show a health colour that means something.
    /// </para>
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// If true, this entry represents a TikTok group chat matched by display name
    /// instead of by username (groups have no profile link / @handle).
    /// </summary>
    public bool IsGroup { get; set; } = false;

    /// <summary>
    /// Message to send to this friend, overriding the shared message on the Dashboard.
    /// <para>
    /// Null or blank means "use the shared message", which is how every entry behaves until the user
    /// opts in. Storing the override per friend lets one contact get a different text - a nickname, an
    /// inside joke, a different language - without changing what everybody else receives.
    /// </para>
    /// <para>
    /// The same <c>{name}</c> / <c>{username}</c> placeholders are still expanded here, so a per-friend
    /// message can be personalised twice over.
    /// </para>
    /// </summary>
    public string? CustomMessage { get; set; }

    /// <summary>True when this entry carries its own message instead of the shared one.</summary>
    public bool HasCustomMessage => !string.IsNullOrWhiteSpace(CustomMessage);

    /// <summary>
    /// A copy of this entry.
    /// <para>
    /// Used while an order is being arranged but not yet saved. The draft has to be able to move entries
    /// around without touching the objects the rest of the app is holding, or an arrangement the user
    /// cancelled would still have changed them.
    /// </para>
    /// </summary>
    public FriendConfig Clone() => new()
    {
        Id = Id,
        Username = Username,
        DisplayName = DisplayName,
        IsEnabled = IsEnabled,
        LastMessageSent = LastMessageSent,
        SuccessCount = SuccessCount,
        FailureCount = FailureCount,
        ConsecutiveFailures = ConsecutiveFailures,
        IsGroup = IsGroup,
        CustomMessage = CustomMessage
    };
}

/// <summary>
/// Represents the result of a streak run
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class StreakRunResult
{
    public DateTime RunTime { get; set; } = DateTime.Now;
    public TimeSpan? Duration { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FriendsErrorMessage => string.Join(',', FriendResults?.Where(x => x.Failed)?.Select(x => x.ErrorMessage) ?? []);
    public List<FriendMessageResult> FriendResults { get; set; } = new();
    public bool Failed => !Success && (!string.IsNullOrEmpty(FriendsErrorMessage) || !string.IsNullOrEmpty(ErrorMessage));

    /// <summary>
    /// True when the whole run was a rehearsal: the automation walked every friend but sent nothing.
    /// Kept on the run as well as on each result so history can label it at a glance.
    /// </summary>
    public bool DryRun { get; set; }
}

/// <summary>
/// Result of sending a message to a specific friend
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class FriendMessageResult
{
    public string FriendId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool Failed => !Success && !string.IsNullOrEmpty(ErrorMessage);
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// Whether the message was actually found in the chat, separately from <see cref="Success"/>.
    /// <list type="bullet">
    ///   <item><description><c>true</c> - a message with the right text was found inside the expected time window.</description></item>
    ///   <item><description><c>false</c> - the chat was readable and the message was not there.</description></item>
    ///   <item><description><c>null</c> - not checked, or the chat could not be read: a failed send, a rehearsal, or an entry written by an older build.</description></item>
    /// </list>
    /// </summary>
    public bool? Verified { get; set; }

    /// <summary>
    /// When this chat's message was sent, recorded at the moment the automation was injected.
    /// <para>
    /// The delivery rescan runs after the whole run has finished and has to decide whether the message is
    /// in the thread. It does that by looking for the text inside a window that opens here - not at the
    /// minute the user scheduled, because a run takes time and a message a few minutes late is still
    /// delivered.
    /// </para>
    /// </summary>
    public DateTime? SentAt { get; set; }

    /// <summary>
    /// The exact text that was sent to this chat.
    /// <para>
    /// Stored because the delivery rescan runs later and has to search the thread for the message that
    /// actually went out. Re-deriving it then would be wrong whenever the run picked a randomised variant,
    /// or the template changed in between: the rescan would look for text that was never sent and report
    /// every chat as missing.
    /// </para>
    /// </summary>
    public string? MessageText { get; set; }

    /// <summary>
    /// True when this entry comes from a dry run, which walks the automation without sending anything.
    /// Dry-run entries are deliberately excluded from every "sent today" figure so a rehearsal can
    /// never masquerade as a real run.
    /// </summary>
    public bool DryRun { get; set; }
}
