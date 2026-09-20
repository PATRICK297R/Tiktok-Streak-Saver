using System.Text.Json;
using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services.Storage;

namespace TiktokStreakSaver.Services;

/// <summary>
/// Service for managing app settings and persistent storage
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class SettingsService
{
    private readonly IAppStorage _storage = AppStorageProvider.Current;

    private const string FriendsListKey = "friends_list";
    private const string MessageTextKey = "message_text";
    private const string LastRunKey = "last_run";
    private const string IsScheduledKey = "is_scheduled";
    private const string RunHistoryKey = "run_history";
    private const string IntervalHoursKey = "interval_hours";
    private const string SkipUnreachableUsersKey = "skip_unreachable_users";
    private const string UseFixedTimeKey = "use_fixed_time";
    private const string FixedTimeHourKey = "fixed_time_hour";
    private const string FixedTimeMinuteKey = "fixed_time_minute";

    /// <summary>Comma-separated "HH:mm" list of daily run times.</summary>
    private const string FixedTimesKey = "fixed_times";

    // ── Resilience: retry tracking + event-driven recovery flags ──
    private const string RetryCountTodayKey = "retry_count_today";
    private const string RetryCountDateKey = "retry_count_date";
    private const string LastRunFailedKey = "last_run_failed";
    private const string LastRunFailureReasonKey = "last_run_failure_reason";
    private const string BatteryAnticipationDateKey = "battery_anticipation_date";
    private const string SendOnBatteryLowKey = "send_on_battery_low";

    // ── Streak risk alerts ──
    private const string StreakRiskAlertsKey = "streak_risk_alerts";
    private const string StreakRiskWarningHourKey = "streak_risk_warning_hour";
    private const string StreakRiskNotifiedDateKey = "streak_risk_notified_date";
    private const string DryRunKey = "dry_run";

    /// <summary>
    /// Maximum hourly retries per calendar day, in addition to the original scheduled run.
    /// </summary>
    public const int MaxRetriesPerDay = 3;

    /// <summary>Failure-reason value: the run was skipped because there was no Wi‑Fi/cellular.</summary>
    public const string FailureReasonNoNetwork = "no_network";

    /// <summary>Failure-reason value: the run completed but one or more messages failed to send.</summary>
    public const string FailureReasonSendError = "send_error";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(AppJsonSerialization.Settings)
        {
            TypeInfoResolver = AppJsonContext.Default
        };
        return options;
    }

    /// <summary>
    /// Default message to send (kept from original; do NOT change to "Streak").
    /// </summary>
    public const string DefaultMessage = "Hey! Keeping our streak alive! \uD83D\uDD25";

    /// <summary>
    /// Default interval in hours.
    /// </summary>
    public const int DefaultIntervalHours = 23;

    /// <summary>
    /// How many runs are kept before the oldest are dropped.
    /// <para>
    /// History is serialised to a single JSON string in Preferences and rewritten on every run, so
    /// this is a deliberate size/latency trade-off rather than "as much as possible": at roughly 1-2 KB
    /// per run, a few hundred entries stays comfortably inside what SharedPreferences handles well.
    /// It was previously 50, which the hourly retry could exhaust within days and which left too
    /// little data for any week-over-week comparison.
    /// </para>
    /// </summary>
    public const int MaxRunHistoryEntries = 200;

    #region Friends List

    private string ReadFriendsListJson()
    {
#if IOS
        var fromFile = Platforms.iOS.Services.IosFriendsFileStorage.ReadJson(migrateFromDefaults: true);
        if (!string.IsNullOrWhiteSpace(fromFile))
            return fromFile;
#endif
        return _storage.GetString(FriendsListKey, string.Empty);
    }

    private bool TryDeserializeFriends(string json, out List<FriendConfig>? friends)
    {
        friends = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            friends = new List<FriendConfig>();
            return true;
        }

        try
        {
            friends = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListFriendConfig);
            if (friends != null)
                return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TryDeserializeFriends source-gen failed: {ex.Message}");
        }

        try
        {
            friends = JsonSerializer.Deserialize<List<FriendConfig>>(json, JsonOptions);
            return friends != null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TryDeserializeFriends fallback failed: {ex.Message}");
            friends = null;
            return false;
        }
    }

    private bool TryLoadFriendsList(out List<FriendConfig>? friends, out string? error)
    {
        friends = null;
        error = null;

        var json = ReadFriendsListJson();
        if (string.IsNullOrWhiteSpace(json))
        {
            friends = new List<FriendConfig>();
            return true;
        }

        if (TryDeserializeFriends(json, out friends))
            return true;

#if IOS
        TryRecoverFriendsListFromStandardDefaults();
        json = ReadFriendsListJson();
        if (!string.IsNullOrWhiteSpace(json) && TryDeserializeFriends(json, out friends))
            return true;

        json = _storage.GetString(FriendsListKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(json) && TryDeserializeFriends(json, out friends))
        {
            Platforms.iOS.Services.IosFriendsFileStorage.WriteJson(json);
            return true;
        }
#endif

        error = "Could not read saved friends list.";
        return false;
    }

    public List<FriendConfig> GetFriendsList()
    {
        if (TryLoadFriendsList(out var friends, out _))
            return friends ?? new List<FriendConfig>();

        return new List<FriendConfig>();
    }

    public bool TrySaveFriendsList(List<FriendConfig> friends, out string? error)
    {
        error = null;
        var json = JsonSerializer.Serialize(friends, AppJsonContext.Default.ListFriendConfig);

        if (!TryDeserializeFriends(json, out _))
        {
            error = "Could not serialize friends list.";
            return false;
        }

#if IOS
        if (!Platforms.iOS.Services.IosFriendsFileStorage.WriteJson(json))
        {
            error = "Could not write friends file.";
            return false;
        }
#endif

        _storage.SetString(FriendsListKey, json);

        var readBack = ReadFriendsListJson();
        if (string.IsNullOrWhiteSpace(readBack) || !TryDeserializeFriends(readBack, out var parsed) || parsed == null)
        {
            error = "Friends list did not persist on this device.";
            return false;
        }

        return true;
    }

    public void SaveFriendsList(List<FriendConfig> friends)
    {
        TrySaveFriendsList(friends, out _);
    }

#if IOS
    private void TryRecoverFriendsListFromStandardDefaults()
    {
        Platforms.iOS.Services.AppGroupAppStorage.TryCopyNonEmptyStringFromStandard(
            FriendsListKey, _storage);
    }
#endif

    public bool TryAddFriend(FriendConfig friend, out string? error)
    {
        if (!TryLoadFriendsList(out var friends, out error) || friends == null)
            friends = new List<FriendConfig>();

        friends.Add(friend);
        return TrySaveFriendsList(friends, out error);
    }

    public void AddFriend(FriendConfig friend)
    {
        TryAddFriend(friend, out _);
    }

    public void RemoveFriend(string friendId)
    {
        var friends = GetFriendsList();
        friends.RemoveAll(f => f.Id == friendId);
        SaveFriendsList(friends);
    }

    public void UpdateFriend(FriendConfig friend)
    {
        var friends = GetFriendsList();
        var index = friends.FindIndex(f => f.Id == friend.Id);
        if (index >= 0)
        {
            friends[index] = friend;
            SaveFriendsList(friends);
        }
    }

    public List<FriendConfig> GetEnabledFriends()
    {
        return GetFriendsList().Where(f => f.IsEnabled).ToList();
    }

    #endregion

    #region Message Configuration

    public string GetMessageText()
    {
        var fromFile = TryReadMessageTextFile();
        if (fromFile != null)
            return fromFile;

        var stored = _storage.GetString(MessageTextKey, string.Empty);
        return string.IsNullOrEmpty(stored) ? DefaultMessage : stored;
    }

    public void SetMessageText(string message)
    {
        var text = message ?? string.Empty;
        _storage.SetString(MessageTextKey, text);
        TryWriteMessageTextFile(text);
    }

    private static string SharedStorageDirectory
    {
        get
        {
#if IOS
            return Platforms.iOS.Services.AppGroupPaths.SharedStorageDirectory;
#else
            return FileSystem.AppDataDirectory;
#endif
        }
    }

    private static string MessageTextFilePath =>
        Path.Combine(SharedStorageDirectory, AppConstants.MessageTextFileName);

    private static string RandomizeMessagesFlagFilePath =>
        Path.Combine(SharedStorageDirectory, AppConstants.RandomizeMessagesFlagFileName);

    private static string? TryReadMessageTextFile()
    {
        try
        {
            if (!File.Exists(MessageTextFilePath))
                return null;

            return File.ReadAllText(MessageTextFilePath);
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteMessageTextFile(string text)
    {
        TryWriteTextFile(MessageTextFilePath, text);
    }

    private static void TryWriteTextFile(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }
        catch
        {
            // UserDefaults / Preferences mirror remains the fallback.
        }
    }

    private static bool? TryReadBoolFlagFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var text = File.ReadAllText(path).Trim();
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1")
                return true;
            if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0")
                return false;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteBoolFlagFile(string path, bool value) =>
        TryWriteTextFile(path, value ? "true" : "false");

    // ── Randomized Normal Messages ──

    private const string RandomizeNormalMessagesKey = "randomize_normal_messages";

    /// <summary>
    /// 50 built-in short streak messages for randomized normal-mode sends.
    /// </summary>
    public static readonly List<string> BuiltInStreakMessages = new()
    {
        "Streak",
        "streak",
        "streakk",
        "streaak",
        "streaakkk",
        "streaaak",
        "strk",
        "Strk",
        "s",
        "S",
        "streaks",
        "Streaks",
        "streakss",
        "streak lol",
        "yo streak",
        "yoo streak",
        "yoo streakk",
        "hey streak",
        "hii streak",
        "hi streak",
        "heyy streak",
        "streak hii",
        "streak hi",
        "streak yo",
        "streak yoo",
        "streakkk",
        "strek",
        "streek",
        "streeek",
        "streeeek",
        "yo",
        "yoo",
        "yooo",
        "hey",
        "hii",
        "heyy",
        "heyyy",
        "here",
        "heree",
        "strkeee",
        "streak rn",
        "quick streak",
        "streakk lol",
        "streak lmao",
        "lol streak",
        "streaaakk",
        "streakkk lol",
        "daily streak",
        "streak streak",
        "ayo streak"
    };

    /// <summary>
    /// Get whether randomized built-in messages are enabled for normal mode
    /// </summary>
    public bool GetRandomizeNormalMessages()
    {
        var fromFile = TryReadBoolFlagFile(RandomizeMessagesFlagFilePath);
        if (fromFile.HasValue)
            return fromFile.Value;

        return _storage.GetBool(RandomizeNormalMessagesKey, false);
    }

    /// <summary>
    /// Set whether randomized built-in messages are enabled for normal mode
    /// </summary>
    public void SetRandomizeNormalMessages(bool enabled)
    {
        _storage.SetBool(RandomizeNormalMessagesKey, enabled);
        TryWriteBoolFlagFile(RandomizeMessagesFlagFilePath, enabled);
    }

    #endregion

    #region Scheduling

    /// <summary>
    /// Interval between automatic runs, in hours (clamped to 1..23).
    /// </summary>
    public int GetIntervalHours()
    {
        var v = (int)_storage.GetLong(IntervalHoursKey, DefaultIntervalHours);
        return Math.Clamp(v, 1, 23);
    }

    /// <summary>
    /// Set the interval in hours (clamped to 1..23).
    /// </summary>
    public void SetIntervalHours(int hours)
    {
        _storage.SetLong(IntervalHoursKey, Math.Clamp(hours, 1, 23));
    }

    public DateTime? GetLastRunTime()
    {
        var ticks = _storage.GetLong(LastRunKey, 0L);
        return ticks > 0 ? new DateTime(ticks) : null;
    }

    public void SetLastRunTime(DateTime time)
    {
        _storage.SetLong(LastRunKey, time.Ticks);
    }

    /// <summary>
    /// Default off (matches fork). Users opt in via the Profile toggle.
    /// </summary>
    public bool IsScheduled()
    {
        return _storage.GetBool(IsScheduledKey, false);
    }

    /// <summary>
    /// Whether automatic streak runs are expected. On iOS this means a Shortcuts automation
    /// (onboarding complete); on Android it follows the in-app schedule toggle.
    /// </summary>
    public bool IsAutomationActive()
    {
#if IOS
        if (IsScheduled())
            return true;

        return _storage.GetBool(AppConstants.IosOnboardingCompleteKey, false);
#else
        return IsScheduled();
#endif
    }

    public void SetScheduled(bool scheduled)
    {
        _storage.SetBool(IsScheduledKey, scheduled);
    }

    /// <summary>
    /// Default ON (kept from original): a missing friend should not abort the whole run.
    /// </summary>
    public bool GetSkipUnreachableUsers()
    {
        return _storage.GetBool(SkipUnreachableUsersKey, true);
    }

    public void SetSkipUnreachableUsers(bool skip)
    {
        _storage.SetBool(SkipUnreachableUsersKey, skip);
    }

    /// <summary>
    /// When the next automated run is due, matching the alarm that will actually fire.
    /// Delegates to <see cref="ScheduleCalculator"/> so the Dashboard label and the schedule
    /// itself can never disagree.
    /// </summary>
    public DateTime GetNextRunTime() =>
        ScheduleCalculator.Next(
            GetUseFixedTime(),
            GetFixedDailyTimes(),
            GetIntervalHours(),
            GetLastRunTime(),
            DateTime.Now);

    /// <summary>
    /// When true, scheduling uses a fixed daily clock time (Hour:Minute) instead of
    /// the rolling interval based on last run.
    /// </summary>
    public bool GetUseFixedTime() => _storage.GetBool(UseFixedTimeKey, false);

    public void SetUseFixedTime(bool value) => _storage.SetBool(UseFixedTimeKey, value);

    /// <summary>
    /// Hour of day (0-23) for fixed daily schedule.
    /// </summary>
    public int GetFixedTimeHour()
    {
        var stored = _storage.GetLong(FixedTimeHourKey, -1);
        return stored >= 0 ? (int)stored : DateTime.Now.Hour;
    }

    public void SetFixedTimeHour(int hour) => _storage.SetLong(FixedTimeHourKey, Math.Clamp(hour, 0, 23));

    /// <summary>
    /// Minute (0-59) for fixed daily schedule.
    /// </summary>
    public int GetFixedTimeMinute()
    {
        var stored = _storage.GetLong(FixedTimeMinuteKey, -1);
        return stored >= 0 ? (int)stored : DateTime.Now.Minute;
    }

    public void SetFixedTimeMinute(int minute) => _storage.SetLong(FixedTimeMinuteKey, Math.Clamp(minute, 0, 59));

    /// <summary>
    /// The daily run times, sorted and de-duplicated. Always returns at least one entry.
    /// <para>
    /// Installs that predate multiple times only have the single hour/minute keys, so those are used
    /// as the fallback. That keeps an existing schedule working across the upgrade instead of
    /// silently resetting it.
    /// </para>
    /// </summary>
    public List<TimeSpan> GetFixedDailyTimes()
    {
        var parsed = ParseFixedTimes(_storage.GetString(FixedTimesKey, string.Empty));
        if (parsed.Count > 0)
            return parsed;

        return new List<TimeSpan> { new(GetFixedTimeHour(), GetFixedTimeMinute(), 0) };
    }

    /// <summary>
    /// Stores the daily run times. An empty collection keeps the currently configured single time
    /// rather than leaving the app with no schedule at all.
    /// </summary>
    public void SetFixedDailyTimes(IEnumerable<TimeSpan>? times)
    {
        var normalized = ScheduleCalculator.NormalizeDailyTimes(times).ToList();

        if (normalized.Count == 0)
            normalized.Add(new TimeSpan(GetFixedTimeHour(), GetFixedTimeMinute(), 0));

        _storage.SetString(FixedTimesKey,
            string.Join(",", normalized.Select(t => $"{t.Hours:D2}:{t.Minutes:D2}")));

        // Keep the legacy single-time keys in step so anything still reading them stays correct.
        SetFixedTimeHour(normalized[0].Hours);
        SetFixedTimeMinute(normalized[0].Minutes);
    }

    private static List<TimeSpan> ParseFixedTimes(string raw)
    {
        var parsed = new List<TimeSpan>();

        if (string.IsNullOrWhiteSpace(raw))
            return parsed;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(':');
            if (bits.Length != 2)
                continue;

            if (!int.TryParse(bits[0], out var hour) || !int.TryParse(bits[1], out var minute))
                continue;

            if (hour is < 0 or > 23 || minute is < 0 or > 59)
                continue;

            parsed.Add(new TimeSpan(hour, minute, 0));
        }

        return ScheduleCalculator.NormalizeDailyTimes(parsed).ToList();
    }

    #endregion

    #region Resilience: retry counter, failure flag, battery toggle

    /// <summary>
    /// Current count of hourly retries used today. Auto-resets when the stored date
    /// stamp is not today's date (so the counter naturally rolls over at midnight).
    /// </summary>
    public int GetTodayRetryCount()
    {
        var storedDateTicks = _storage.GetLong(RetryCountDateKey, 0L);
        var today = DateTime.Now.Date.Ticks;
        if (storedDateTicks != today)
            return 0;
        return (int)_storage.GetLong(RetryCountTodayKey, 0);
    }

    /// <summary>
    /// Increments today's retry counter (rolling the date stamp if it's a new day)
    /// and returns the new value.
    /// </summary>
    public int IncrementTodayRetryCount()
    {
        var today = DateTime.Now.Date.Ticks;
        var storedDateTicks = _storage.GetLong(RetryCountDateKey, 0L);
        var current = storedDateTicks == today ? (int)_storage.GetLong(RetryCountTodayKey, 0) : 0;
        var next = current + 1;
        _storage.SetLong(RetryCountDateKey, today);
        _storage.SetLong(RetryCountTodayKey, next);
        return next;
    }

    /// <summary>Reset today's retry counter (typically on a fully successful run).</summary>
    public void ResetTodayRetryCount()
    {
        _storage.SetLong(RetryCountDateKey, DateTime.Now.Date.Ticks);
        _storage.SetLong(RetryCountTodayKey, 0);
    }

    /// <summary>
    /// Flag indicating that the last attempted run failed and a recovery (e.g. on
    /// network restore) may be worth attempting.
    /// </summary>
    public bool GetLastRunFailed() => _storage.GetBool(LastRunFailedKey, false);

    /// <summary>Set / clear the last-run-failed flag, optionally tagging the reason.</summary>
    public void SetLastRunFailed(bool failed, string? reason)
    {
        _storage.SetBool(LastRunFailedKey, failed);
        if (failed && !string.IsNullOrEmpty(reason))
            _storage.SetString(LastRunFailureReasonKey, reason);
        else
            _storage.Remove(LastRunFailureReasonKey);
    }

    /// <summary>Reason tag set the last time the run failed, or null when cleared.</summary>
    public string? GetLastRunFailureReason()
    {
        var v = _storage.GetString(LastRunFailureReasonKey, string.Empty);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>
    /// True if the battery-low anticipation already fired today (we only fire it once
    /// per calendar day to avoid loops if the device keeps oscillating around 15%).
    /// </summary>
    public bool WasBatteryAnticipationUsedToday()
    {
        var storedTicks = _storage.GetLong(BatteryAnticipationDateKey, 0L);
        return storedTicks == DateTime.Now.Date.Ticks;
    }

    /// <summary>Mark today as already used for battery-low anticipation.</summary>
    public void MarkBatteryAnticipationUsedToday()
    {
        _storage.SetLong(BatteryAnticipationDateKey, DateTime.Now.Date.Ticks);
    }

    /// <summary>
    /// User toggle: when ON, the app will start the streak run early if Android fires
    /// the system battery-low broadcast and today's streak hasn't been sent yet.
    /// Default ON.
    /// </summary>
    public bool GetSendOnBatteryLow() => _storage.GetBool(SendOnBatteryLowKey, true);

    public void SetSendOnBatteryLow(bool value) => _storage.SetBool(SendOnBatteryLowKey, value);

    #endregion

    #region Streak risk alerts

    /// <summary>
    /// User toggle: warn when friends are still unsent once the day is running out.
    /// Default ON - the point of the app is not losing streaks, and a silent failure is the worst case.
    /// </summary>
    public bool GetStreakRiskAlerts() => _storage.GetBool(StreakRiskAlertsKey, true);

    public void SetStreakRiskAlerts(bool value) => _storage.SetBool(StreakRiskAlertsKey, value);

    /// <summary>
    /// Rehearsal mode: run the whole automation but send nothing, so the user can confirm every friend
    /// is reachable before trusting it with real messages. Off by default - it is a diagnostic.
    /// </summary>
    public bool GetDryRun() => _storage.GetBool(DryRunKey, false);

    public void SetDryRun(bool value) => _storage.SetBool(DryRunKey, value);

    /// <summary>Local hour after which an unsent streak is reported as at risk.</summary>
    public int GetStreakRiskWarningHour()
    {
        var stored = (int)_storage.GetLong(StreakRiskWarningHourKey, StreakRiskEvaluator.DefaultWarningHour);
        return Math.Clamp(stored, 0, 23);
    }

    public void SetStreakRiskWarningHour(int hour) =>
        _storage.SetLong(StreakRiskWarningHourKey, Math.Clamp(hour, 0, 23));

    /// <summary>
    /// True when the at-risk warning was already shown today. One warning per day is enough; repeating
    /// it would train the user to ignore it.
    /// </summary>
    public bool WasRiskAlertSentToday() =>
        _storage.GetLong(StreakRiskNotifiedDateKey, 0L) == DateTime.Now.Date.Ticks;

    public void MarkRiskAlertSentToday() =>
        _storage.SetLong(StreakRiskNotifiedDateKey, DateTime.Now.Date.Ticks);

    #endregion

    #region Run History

    public List<StreakRunResult> GetRunHistory()
    {
#if IOS
        var fromFile = Platforms.iOS.Services.IosRunHistoryFileStorage.ReadHistory();
        if (fromFile.Count > 0)
            return fromFile;
#endif

        var json = _storage.GetString(RunHistoryKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
            return new List<StreakRunResult>();

        try
        {
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.ListStreakRunResult)
                ?? new List<StreakRunResult>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetRunHistory deserialize failed: {ex.Message}");
#if IOS
            Platforms.iOS.Services.IosRunTrace.Append($"GetRunHistory deserialize failed: {ex.Message}");
#endif
            return new List<StreakRunResult>();
        }
    }

    public void AddRunResult(StreakRunResult result)
    {
        var history = GetRunHistory();
        history.Insert(0, result);

        if (history.Count > MaxRunHistoryEntries)
            history = history.Take(MaxRunHistoryEntries).ToList();

        var json = JsonSerializer.Serialize(history, AppJsonContext.Default.ListStreakRunResult);
        _storage.SetString(RunHistoryKey, json);
#if IOS
        Platforms.iOS.Services.IosRunHistoryFileStorage.WriteHistory(history);
#endif
    }

    #endregion

    #region Clear Data

    public void ClearRunHistory()
    {
        _storage.Remove(RunHistoryKey);
#if IOS
        Platforms.iOS.Services.IosRunHistoryFileStorage.Clear();
#endif
    }

    public void ClearAll()
    {
        _storage.Clear();
    }

    #endregion
}
