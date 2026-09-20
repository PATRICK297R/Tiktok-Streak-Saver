namespace TiktokStreakSaver;

/// <summary>
/// Centralized package and intent constants (keep in sync with ApplicationId in csproj).
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class AppConstants
{
    public const string PackageName = "com.jon2g.tiktokstreaksaver";

    public const string ActionStreakAlarm = PackageName + ".ACTION_STREAK_ALARM";

    /// <summary>Daily check that warns when streaks are still unsent late in the day.</summary>
    public const string ActionStreakRiskAlarm = PackageName + ".ACTION_STREAK_RISK_ALARM";

    /// <summary>App Group for shared settings, cookies, and extension state.</summary>
    public const string AppGroupId = "group.com.jon2g.tiktokstreaksaver";

    public const string SharedCookiesFileName = "cookies.json";

    public const string FriendsListFileName = "friends_list.json";

    public const string SessionStateFileName = "session_state.json";

    public const string AuthRequiredKey = "auth_required";

    public const string ForceManualRunKey = "force_manual_run";

    public const string LastRunFailureReasonKey = "last_run_failure_reason";

    public const string RunLogsFileName = "run_logs.txt";

    /// <summary>
    /// Durable record of the sends attempted today, written before each message goes out.
    /// <para>
    /// Separate from the run log because it is read by the service at the start of a run to decide who to
    /// leave alone, and because it must be written and flushed to disk <em>before</em> a message is sent -
    /// which is the only way a run killed mid-send can be prevented from sending the same message again.
    /// </para>
    /// </summary>
    public const string PendingSendsFileName = "pending_sends.txt";

    /// <summary>
    /// The chat import's progress and results. Kept on disk so an import started in the service can be
    /// collected by the page later, including after the app was closed and reopened.
    /// </summary>
    public const string ContactScanFileName = "contact_scan.json";

    /// <summary>Stops a chat import that is running. Sent to the import service by the page and by the notification.</summary>
    public const string ActionStopContactScan = PackageName + ".ACTION_STOP_CONTACT_SCAN";

    /// <summary>
    /// Which language the interface is drawn in. Absent means English, which is the default and what every
    /// existing install keeps.
    /// </summary>
    public const string LanguageKey = "app_language";

    /// <summary>
    /// Carried by a chat-import notification: tapping it should land on the import page rather than on
    /// whichever tab happened to be open, because that is where the results are.
    /// </summary>
    public const string ExtraOpenContactImport = PackageName + ".EXTRA_OPEN_CONTACT_IMPORT";

    public const string RunHistoryFileName = "run_history.json";

    public const string MauiRunTraceFileName = "maui_run_trace.txt";

    public const string LastRunLogSnippetKey = "last_run_log_snippet";

    public const string MessageTextFileName = "message_text.txt";

    public const string RandomizeMessagesFlagFileName = "randomize_normal_messages.bool";

    public const string IosOnboardingCompleteKey = "ios_onboarding_complete";

    public const string DesktopChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
}
