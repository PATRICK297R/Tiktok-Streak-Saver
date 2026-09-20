namespace TiktokStreakSaver.Models;

/// <summary>
/// A backup of everything the user has configured, so it survives an uninstall.
/// <para>
/// Uninstalling an Android app deletes its private storage, and no manifest flag can change that -
/// <c>hasFragileUserData</c> only offers a "Keep app data" checkbox at uninstall time, which the user
/// can miss. A file the user owns is the only thing that reliably outlives a reinstall, and it also
/// moves to a new phone, which no manifest setting can do.
/// </para>
/// <para>
/// The TikTok session is deliberately <em>not</em> part of this. Cookies are full access to the
/// account, and writing them to a shareable file as plain text would be a worse problem than the one
/// being solved; the app asks for a fresh login instead.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class AppBackup
{
    /// <summary>
    /// Version of the backup file itself, separate from the app version, so a file written by a
    /// newer build can be recognised and refused instead of being half-understood.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>The app version that wrote the file, shown back to the user on restore.</summary>
    public string AppVersion { get; set; } = string.Empty;

    public DateTime ExportedAt { get; set; } = DateTime.Now;

    /// <summary>Friends and groups, in send order. The order is the queue, so it is preserved.</summary>
    public List<FriendConfig> Friends { get; set; } = new();

    public BackupSettings Settings { get; set; } = new();

    /// <summary>Past runs, so History does not start empty on a new install.</summary>
    public List<StreakRunResult> RunHistory { get; set; } = new();

    public string SessionDisplayName { get; set; } = string.Empty;
}

/// <summary>
/// The user-facing configuration half of a backup.
/// <para>
/// Per-day bookkeeping is deliberately left out: the retry counter, the "already alerted today"
/// stamps and the battery-anticipation date all describe what one device did on one day. Restoring
/// them would tell a freshly installed app that it had already warned about a streak today, so the
/// warning the user actually needs would be suppressed.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class BackupSettings
{
    public string MessageText { get; set; } = string.Empty;

    public bool RandomizeNormalMessages { get; set; }

    public bool Scheduled { get; set; }

    public bool UseFixedTime { get; set; }

    public int FixedTimeHour { get; set; }

    public int FixedTimeMinute { get; set; }

    /// <summary>Daily run times as "HH:mm", the same text the app stores internally.</summary>
    public List<string> FixedDailyTimes { get; set; } = new();

    public int IntervalHours { get; set; }

    public bool SkipUnreachableUsers { get; set; }

    public bool SendOnBatteryLow { get; set; }

    public bool StreakRiskAlerts { get; set; }

    public int StreakRiskWarningHour { get; set; }

    public bool DryRun { get; set; }

    /// <summary>
    /// When the last run happened, so the Dashboard does not claim "Never" after a restore. Null on
    /// a backup written before any run.
    /// </summary>
    public DateTime? LastRunTime { get; set; }
}
