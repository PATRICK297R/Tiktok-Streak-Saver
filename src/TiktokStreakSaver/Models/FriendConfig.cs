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
    /// Whether the send was actually confirmed, separately from <see cref="Success"/>.
    /// <list type="bullet">
    ///   <item><description><c>true</c> - the composer cleared, so TikTok accepted the message.</description></item>
    ///   <item><description><c>false</c> - the script reported success but nothing confirmed it.</description></item>
    ///   <item><description><c>null</c> - not checked: a failed send, or an entry written by an older build.</description></item>
    /// </list>
    /// </summary>
    public bool? Verified { get; set; }

    /// <summary>
    /// True when this entry comes from a dry run, which walks the automation without sending anything.
    /// Dry-run entries are deliberately excluded from every "sent today" figure so a rehearsal can
    /// never masquerade as a real run.
    /// </summary>
    public bool DryRun { get; set; }
}
