namespace TiktokStreakSaver.Services;

using TiktokStreakSaver.Models;

/// <summary>
/// What a restore actually changed, so the page can report facts instead of a vague success.
/// </summary>
/// <param name="FriendsAdded">Entries from the backup that were new to this device.</param>
/// <param name="FriendsSkipped">Entries already present, matched by id or by identity.</param>
/// <param name="FriendsTotal">Size of the list after restoring.</param>
/// <param name="HistoryRestored">Whether past runs were written back.</param>
/// <param name="ReplacedExistingList">True when the backup replaced the list instead of merging.</param>
public readonly record struct BackupApplyResult(
    int FriendsAdded,
    int FriendsSkipped,
    int FriendsTotal,
    bool HistoryRestored,
    bool ReplacedExistingList);

/// <summary>
/// Builds a backup from what is on the device, and writes one back.
/// <para>
/// The decisions live in <see cref="BackupMergePolicy"/>, <see cref="BackupTimes"/> and
/// <see cref="AppBackupCodec"/>, which are all unit tested; this class is the thin plumbing that
/// talks to storage and to the Android alarm schedulers.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class BackupService
{
    private readonly SettingsService _settings;
    private readonly SessionService _session;

    public BackupService() : this(new SettingsService(), new SessionService())
    {
    }

    public BackupService(SettingsService settings, SessionService session)
    {
        _settings = settings;
        _session = session;
    }

    /// <summary>
    /// Everything the user configured. The TikTok session is intentionally absent: cookies are full
    /// access to the account and this file is meant to be shared or moved between devices.
    /// </summary>
    public AppBackup Create() => new()
    {
        FormatVersion = AppBackup.CurrentFormatVersion,
        AppVersion = AppInfo.Current.VersionString,
        ExportedAt = DateTime.Now,
        Friends = _settings.GetFriendsList(),
        RunHistory = _settings.GetRunHistory(),
        SessionDisplayName = _session.GetDisplayName(),
        Settings = new BackupSettings
        {
            MessageText = _settings.GetMessageText(),
            RandomizeNormalMessages = _settings.GetRandomizeNormalMessages(),
            Scheduled = _settings.IsScheduled(),
            UseFixedTime = _settings.GetUseFixedTime(),
            FixedTimeHour = _settings.GetFixedTimeHour(),
            FixedTimeMinute = _settings.GetFixedTimeMinute(),
            FixedDailyTimes = BackupTimes.Format(_settings.GetFixedDailyTimes()),
            IntervalHours = _settings.GetIntervalHours(),
            SkipUnreachableUsers = _settings.GetSkipUnreachableUsers(),
            SendOnBatteryLow = _settings.GetSendOnBatteryLow(),
            StreakRiskAlerts = _settings.GetStreakRiskAlerts(),
            StreakRiskWarningHour = _settings.GetStreakRiskWarningHour(),
            DryRun = _settings.GetDryRun(),
            LastRunTime = _settings.GetLastRunTime()
        }
    };

    /// <summary>
    /// A file name that says what it is and when it was made, so several backups can sit in the same
    /// Downloads folder without overwriting each other.
    /// </summary>
    public static string SuggestedFileName(DateTime now) => $"streak-saver-backup-{now:yyyyMMdd-HHmm}.json";

    /// <summary>
    /// Writes a backup back onto the device.
    /// <para>
    /// The list is merged by default so a restore can never delete something the user added after the
    /// backup was written; <paramref name="replaceExisting"/> is the explicit opt-in for the other
    /// behaviour.
    /// </para>
    /// <para>
    /// Past runs and the last-run time are only restored when the device has none, or when the list is
    /// being replaced. Overwriting newer records with older ones would make History a step backwards.
    /// </para>
    /// </summary>
    public bool TryApply(AppBackup backup, bool replaceExisting, out BackupApplyResult result, out string? error)
    {
        result = default;
        error = null;

        var incoming = backup.Friends ?? new List<FriendConfig>();
        var current = _settings.GetFriendsList();

        List<FriendConfig> friends;
        int added;
        int skipped;

        if (replaceExisting)
        {
            friends = BackupMergePolicy.Replace(incoming);
            added = friends.Count;
            skipped = 0;
        }
        else
        {
            var merge = BackupMergePolicy.Merge(current, incoming);
            friends = merge.Friends;
            added = merge.Added;
            skipped = merge.Skipped;
        }

        // Saved first and checked: if the list cannot be written, nothing else should be applied on
        // top of a restore that half happened.
        if (!_settings.TrySaveFriendsList(friends, out error))
            return false;

        var settings = backup.Settings ?? new BackupSettings();

        // Only when the backup carries one. Writing a blank would throw away the built-in default
        // that an install starts with.
        if (!string.IsNullOrWhiteSpace(settings.MessageText))
            _settings.SetMessageText(settings.MessageText);

        _settings.SetRandomizeNormalMessages(settings.RandomizeNormalMessages);
        _settings.SetUseFixedTime(settings.UseFixedTime);

        var restoredTimes = BackupTimes.Parse(settings.FixedDailyTimes);
        if (restoredTimes.Count > 0)
            _settings.SetFixedDailyTimes(restoredTimes);

        _settings.SetIntervalHours(settings.IntervalHours);
        _settings.SetSkipUnreachableUsers(settings.SkipUnreachableUsers);
        _settings.SetSendOnBatteryLow(settings.SendOnBatteryLow);
        _settings.SetStreakRiskAlerts(settings.StreakRiskAlerts);
        _settings.SetStreakRiskWarningHour(settings.StreakRiskWarningHour);
        _settings.SetDryRun(settings.DryRun);
        _settings.SetScheduled(settings.Scheduled);

        bool historyRestored = false;
        var history = backup.RunHistory ?? new List<StreakRunResult>();
        if (history.Count > 0 && (replaceExisting || _settings.GetRunHistory().Count == 0))
        {
            // AddRunResult inserts at the front, so the newest-first order survives only if the list
            // is walked backwards.
            _settings.ClearRunHistory();
            for (var i = history.Count - 1; i >= 0; i--)
            {
                var run = history[i];
                if (run is not null)
                    _settings.AddRunResult(run);
            }

            historyRestored = true;
        }

        if (settings.LastRunTime.HasValue && (replaceExisting || _settings.GetLastRunTime() is null))
            _settings.SetLastRunTime(settings.LastRunTime.Value);

        if (!string.IsNullOrWhiteSpace(backup.SessionDisplayName))
            _session.SetDisplayName(backup.SessionDisplayName);

        RearmSchedulers();

        result = new BackupApplyResult(added, skipped, friends.Count, historyRestored, replaceExisting);
        return true;
    }

    /// <summary>
    /// Puts the Android alarms back in step with the restored flags.
    /// <para>
    /// <see cref="SettingsService.SetScheduled"/> only writes a flag; the alarms that make it true are
    /// armed separately. Restoring without this would leave the toggle reading "on" while nothing was
    /// ever scheduled, so streaks would silently stop.
    /// </para>
    /// </summary>
    private void RearmSchedulers()
    {
#if ANDROID
        try
        {
            var context = global::Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                          ?? global::Android.App.Application.Context;

            if (context is null)
                return;

            if (_settings.IsScheduled())
                Platforms.Android.StreakScheduler.ScheduleNextRun(context);
            else
                Platforms.Android.StreakScheduler.CancelSchedule(context);

            // The risk alert is a second alarm with its own toggle.
            if (_settings.GetStreakRiskAlerts())
                Platforms.Android.StreakRiskScheduler.ScheduleNext(context);
            else
                Platforms.Android.StreakRiskScheduler.Cancel(context);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Rescheduling after restore failed: {ex}");
        }
#endif
    }
}
