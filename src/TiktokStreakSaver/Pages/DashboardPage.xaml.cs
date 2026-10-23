using Microsoft.Maui.Controls.Shapes;
using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using TiktokStreakSaver.Services.Storage;
using TiktokStreakSaver.Views;

namespace TiktokStreakSaver.Pages;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class DashboardPage : ContentPage
{
    private readonly SettingsService _settingsService;
    private readonly SessionService _sessionService;
#if ANDROID
    private readonly UpdateService _updateService;
    private bool _isCheckingForUpdates = false;
#endif
    private bool _isAppInForeground = false;
    private bool _suppressMessagePersist;
    private bool _messageSettingsLoaded;
    private string _messageDraft = string.Empty;
    private IDispatcherTimer? _statusTimer;
    private readonly NormalProgressDrawable _normalProgressDrawable;

    /// <summary>Chats the retry button would run for, refreshed with the status labels.</summary>
    private List<string> _retryableFailedIds = new();

    public DashboardPage()
    {
        InitializeComponent();
        _settingsService = new SettingsService();
        _sessionService = new SessionService();
#if ANDROID
        _updateService = new UpdateService();
#endif

        _normalProgressDrawable = new NormalProgressDrawable();
        OverviewProgressGraphicsView.Drawable = _normalProgressDrawable;
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await RefreshSessionUiAsync();
            ApplyMessageEditorMode(GetIsRunInProgress());
        });
    }

    private static bool GetIsRunInProgress()
    {
#if ANDROID
        return TiktokStreakSaver.Platforms.Android.Services.StreakService.IsRunning;
#elif IOS
        return Platforms.iOS.Services.IosStreakRunner.IsRunning;
#else
        return false;
#endif
    }

    private Color GetThemeColor(string key, string fallbackHex = "#92979E")
    {
        if (Application.Current != null && Application.Current.Resources.TryGetValue(key, out var resource) && resource is Color color)
            return color;
        return Color.FromArgb(fallbackHex);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _isAppInForeground = true;
        SessionState.Changed -= OnSessionStateChanged;
        SessionState.Changed += OnSessionStateChanged;

#if IOS
        PullToRefreshHintLabel.IsVisible = false;
        NextRunTitleLabel.Text = Localization.T("Dash_Automation");
#endif

        this.Opacity = 0;
        this.TranslationY = 12;
        await Task.WhenAll(
            this.FadeTo(1, 280, Easing.SinInOut),
            this.TranslateTo(0, 0, 280, Easing.SinInOut));

        GreetingLabel.Text = string.Format(Localization.T("Dash_Greeting"), _sessionService.GetDisplayName());

        LoadProfilePhoto();
        await RefreshSessionUiAsync();
        LoadSettings();
        UpdateStatus();

        await EvaluatePermissionsAsync();

        if (_statusTimer == null)
        {
            _statusTimer = Dispatcher.CreateTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(1);
            _statusTimer.Tick += OnStatusTimerTick;
        }
        _statusTimer.Start();
        OnStatusTimerTick(null, EventArgs.Empty);

        _ = CheckStartupPopupAsync();
    }

    protected override void OnDisappearing()
    {
        if (MessageEditor.IsFocused)
            MessageEditor.Unfocus();

        PersistMessageText();
        base.OnDisappearing();
        _isAppInForeground = false;
        SessionState.Changed -= OnSessionStateChanged;
        if (_statusTimer != null)
        {
            _statusTimer.Stop();
            _statusTimer.Tick -= OnStatusTimerTick;
            _statusTimer = null;
        }
    }

    private void LoadProfilePhoto()
    {
        var photoPath = _sessionService.GetProfileImagePath();
        if (!string.IsNullOrEmpty(photoPath) && System.IO.File.Exists(photoPath))
        {
            ProfileAvatarImage.Source = ImageSource.FromFile(photoPath);
            ProfileAvatarImage.IsVisible = true;
            ProfileAvatarEmoji.IsVisible = false;
            ProfileAvatarImage.Clip = new EllipseGeometry
            {
                Center = new Point(22, 22),
                RadiusX = 22,
                RadiusY = 22
            };
        }
        else
        {
            ProfileAvatarImage.IsVisible = false;
            ProfileAvatarEmoji.IsVisible = true;
        }
    }

    private void UpdateSessionIndicator(bool runReady)
    {
        MasterRunButton.IsEnabled = true;
        MasterRunButton.Opacity = 1.0;
        MasterRunButton.Text = runReady ? Localization.T("Dash_RunNow") : Localization.T("Dash_LoginRequired");
    }

    private async Task RefreshSessionUiAsync()
    {
        bool runReady = await SessionRefreshHelper.RefreshAndGetRunReadyAsync(_sessionService);
        UpdateSessionIndicator(runReady);
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        bool isRunning = GetIsRunInProgress();
        RunButtonsContainer.IsVisible = !isRunning;
        StopServiceButton.IsVisible = isRunning;

        ApplyMessageEditorMode(isRunning);

        UpdateStatus();
    }

    private void PersistMessageText()
    {
        if (_suppressMessagePersist)
            return;

        var text = _messageDraft;
        if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(MessageEditor.Text))
            text = MessageEditor.Text;

        _settingsService.SetMessageText(text ?? string.Empty);
    }

    private void SetMessageEditorText(string text)
    {
        _messageDraft = text;

        if (string.Equals(MessageEditor.Text, text, StringComparison.Ordinal))
            return;

        _suppressMessagePersist = true;
        try
        {
            MessageEditor.Text = text;
        }
        finally
        {
            _suppressMessagePersist = false;
        }
    }

    private void ApplyMessageEditorMode(bool isRunning)
    {
        var isRandomized = _settingsService.GetRandomizeNormalMessages();
        CustomMessagePanel.IsVisible = !isRandomized;
        RandomizeModeHint.IsVisible = isRandomized;
        MessageEditor.IsEnabled = !isRunning && !isRandomized;
        MessageEditor.Opacity = isRunning ? 0.6 : 1.0;
    }

    private static string NormalizeVersion(string raw)
        => raw.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? raw.Substring(1) : raw;

    private async Task CheckStartupPopupAsync()
    {
#if ANDROID
        if (_isCheckingForUpdates) return;
        _isCheckingForUpdates = true;
#endif
        try
        {
            if (!_isAppInForeground) return;
            if (Navigation.ModalStack.Any(p => p is AboutPopupPage)) return;

            string currentVersion = NormalizeVersion(AppInfo.Current.VersionString);

#if ANDROID
            bool updateJustInstalled = Preferences.Default.Get("UpdateJustInstalled", false);
            if (updateJustInstalled)
            {
                Preferences.Default.Remove("UpdateJustInstalled");
                Preferences.Default.Set("LastAppVersionSeen", currentVersion);
                _isCheckingForUpdates = false;
                await CheckUpdateOnlyAsync();
                return;
            }
#endif

            string lastAppSeen = NormalizeVersion(Preferences.Default.Get("LastAppVersionSeen", string.Empty));
            if (string.IsNullOrEmpty(lastAppSeen) || lastAppSeen != currentVersion)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                    await Navigation.PushModalAsync(new AboutPopupPage(
                        Localization.T("About_Welcome"), currentVersion, string.Empty, false)));
                return;
            }

#if ANDROID
            _isCheckingForUpdates = false;
            await CheckUpdateOnlyAsync();
#endif
        }
        catch { }
#if ANDROID
        finally { _isCheckingForUpdates = false; }
#endif
    }

#if ANDROID
    private async Task CheckUpdateOnlyAsync()
    {
        if (_isCheckingForUpdates) return;
        _isCheckingForUpdates = true;
        try
        {
            if (!_isAppInForeground) return;
            if (Navigation.ModalStack.Any(p => p is AboutPopupPage)) return;

            string currentVersion = NormalizeVersion(AppInfo.Current.VersionString);
            string lastRemoteSeen = NormalizeVersion(Preferences.Default.Get("LastRemoteVersionSeen", string.Empty));

            var updateCheck = await _updateService.CheckForUpdatesAsync();
            if (updateCheck == null || !updateCheck.HasUpdate) return;

            string remoteVersion = NormalizeVersion(updateCheck.LatestVersion);
            if (remoteVersion == lastRemoteSeen || remoteVersion == currentVersion) return;
            if (Navigation.ModalStack.Any(p => p is AboutPopupPage)) return;

            await MainThread.InvokeOnMainThreadAsync(async () =>
                await Navigation.PushModalAsync(new AboutPopupPage(
                    Localization.T("Dash_UpdateAvailable"), remoteVersion, updateCheck.Changelog, true, updateCheck.ApkDownloadUrl)));
        }
        catch { }
        finally { _isCheckingForUpdates = false; }
    }
#endif

    private void LoadSettings()
    {
        if (!_messageSettingsLoaded)
        {
            SetMessageEditorText(_settingsService.GetMessageText());
            _messageSettingsLoaded = true;
        }

        ApplyMessageEditorMode(GetIsRunInProgress());
    }

    private void UpdateStatus()
    {
        var isScheduled = _settingsService.IsAutomationActive();
        // Formatted by the shared helper so this label and the Activity page line can never disagree
        // about when the last run happened.
        LastRunLabel.Text = RunRecency.DescribeLastRun(_settingsService.GetLastRunTime(), DateTime.Now);

        if (isScheduled)
        {
#if IOS
            NextRunTitleLabel.Text = Localization.T("Dash_Automation");
            NextRunLabel.Text = Localization.T("Dash_ShortcutsApp");
#else
            NextRunLabel.Text = FormatNextRun(_settingsService.GetNextRunTime());
#endif
        }
#if IOS
        else
        {
            NextRunTitleLabel.Text = Localization.T("Dash_Automation");
            NextRunLabel.Text = Localization.T("Dash_SetUpInProfile");
        }
#else
        else NextRunLabel.Text = Localization.T("Dash_NotScheduled");
#endif

        var history = _settingsService.GetRunHistory();
        var currentEnabledFriends = _settingsService.GetEnabledFriends();

        // The in-flight run is not in history yet - CompleteService writes it - so without this the
        // bar sat empty for the whole run and only filled at the moment it finished. Appending the
        // live copy makes the bar, the counters and the per-chat state move as the run progresses.
        var progressSource = WithLiveRun(history);

        // Summed across every run of the day rather than read off the newest entry, so a run that
        // skipped every friend (already messaged today) cannot reset the counters. See
        // DailyProgressCalculator for the full reasoning.
        var progress = DailyProgressCalculator.Calculate(
            progressSource, currentEnabledFriends.Count, DateTime.Today);

        bool ranToday = progress.RanToday;
        int sentToday = progress.SentToday;
        int successPercent = progress.SuccessPercent;
        int remainingToday = progress.Remaining;
        string progressText = progress.ProgressText;
        float progressFraction = progress.Fraction;

        OverviewSentLabel.Text = sentToday.ToString();
        OverviewSuccessLabel.Text = ranToday ? $"{successPercent}%" : "--";
        OverviewRemainingLabel.Text = remainingToday.ToString();

        OverviewProgressLabel.Text = progressText;
        _normalProgressDrawable.Progress = progressFraction;
        _normalProgressDrawable.IsDarkTheme = Application.Current?.RequestedTheme == AppTheme.Dark;
        OverviewProgressGraphicsView.Invalidate();

        UpdateRetryFailedButton(progressSource, currentEnabledFriends);
    }

    /// <summary>
    /// Today's history plus the run currently in flight, if there is one.
    /// <para>
    /// The live entry is a snapshot, not the service's own object: the service keeps appending to
    /// that list while this is being read on the UI thread, and enumerating it directly would throw
    /// when the collection changed mid-loop.
    /// </para>
    /// </summary>
    private static List<StreakRunResult> WithLiveRun(List<StreakRunResult> history)
    {
        var combined = new List<StreakRunResult>(history);

#if ANDROID
        var liveResults = TiktokStreakSaver.Platforms.Android.Services.StreakService.LiveRunResults();
        if (liveResults.Count > 0)
        {
            combined.Add(new StreakRunResult
            {
                RunTime = DateTime.Now,
                DryRun = liveResults.All(r => r.DryRun),
                FriendResults = liveResults,
            });
        }
#endif

        return combined;
    }

    /// <summary>
    /// Shows the retry button only when today's runs left chats that failed and are still enabled.
    /// <para>
    /// A chat that already succeeded later in the day is not retryable: the day's progress counts each
    /// chat once, so retrying it would send a second message for a streak that is already safe.
    /// </para>
    /// </summary>
    private void UpdateRetryFailedButton(List<StreakRunResult> history, List<FriendConfig> enabledFriends)
    {
#if IOS
        // Targeted retry starts the Android background service, which has no iOS equivalent.
        RetryFailedButton.IsVisible = false;
        _retryableFailedIds = new List<string>();
        return;
#else
        _retryableFailedIds = GetRetryableFailedFriendIds(history, enabledFriends);
        RetryFailedButton.IsVisible = _retryableFailedIds.Count > 0;
#endif
    }

    /// <summary>
    /// Ids of chats that failed today and have not succeeded since.
    /// </summary>
    internal static List<string> GetRetryableFailedFriendIds(
        List<StreakRunResult> history, List<FriendConfig> enabledFriends)
    {
        if (history is null || enabledFriends is null)
            return new List<string>();

        var enabledIds = enabledFriends
            .Where(f => f.IsEnabled)
            .Select(f => f.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (enabledIds.Count == 0)
            return new List<string>();

        var today = DateTime.Today;
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var succeeded = new HashSet<string>(StringComparer.Ordinal);

        // Oldest first, so a later success can clear an earlier failure within the same day.
        foreach (var run in history.Where(r => r.RunTime.Date == today).OrderBy(r => r.RunTime))
        {
            foreach (var result in run.FriendResults)
            {
                // A rehearsal sends nothing, so it can neither fail nor fix a chat.
                if (result.DryRun)
                    continue;

                if (result.Success)
                {
                    succeeded.Add(result.FriendId);
                    failed.Remove(result.FriendId);
                }
                else if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    if (!succeeded.Contains(result.FriendId))
                        failed.Add(result.FriendId);
                }
            }
        }

        return failed.Where(id => enabledIds.Contains(id)).ToList();
    }

    /// <summary>
    /// Human-readable next-run label.
    /// Imminent runs use a relative phrasing ("Now", "In 12 minutes"); future runs are anchored to
    /// the calendar day ("Today at 09:00", "Tomorrow at 09:00", "Wed, May 14 at 09:00") so the
    /// fixed-daily-schedule mode reads naturally even when the next run is up to a day away.
    /// </summary>
    private static string FormatNextRun(DateTime nextRun)
    {
        var now = DateTime.Now;
        var timeUntil = nextRun - now;

        if (timeUntil.TotalSeconds < 30)
            return Localization.T("Dash_NextRunNow");
        if (timeUntil.TotalMinutes < 1)
            return Localization.T("Dash_NextRunInLessThanMinute");
        if (timeUntil.TotalMinutes < 60)
            return string.Format(Localization.T("Dash_NextRunInMinutes"), (int)timeUntil.TotalMinutes);

        var time = nextRun.ToString("HH:mm");
        var nextDate = nextRun.Date;
        var today = now.Date;

        if (nextDate == today)
            return string.Format(Localization.T("Dash_NextRunToday"), time);
        if (nextDate == today.AddDays(1))
            return string.Format(Localization.T("Dash_NextRunTomorrow"), time);

        return string.Format(Localization.T("Dash_NextRunOnDate"), nextRun.ToString("ddd, MMM dd"), time);
    }

    private void OnMessageChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressMessagePersist)
            return;

        _messageDraft = e.NewTextValue ?? string.Empty;
        _settingsService.SetMessageText(_messageDraft);
    }

    private void OnMessageUnfocused(object? sender, FocusEventArgs e)
    {
        _messageDraft = MessageEditor.Text ?? _messageDraft;
        PersistMessageText();
    }

    private void OnMessageCompleted(object? sender, EventArgs e)
    {
        _messageDraft = MessageEditor.Text ?? _messageDraft;
        PersistMessageText();
    }

    private async void OnMasterRunClicked(object? sender, EventArgs e)
    {
        await MasterRunButton.ScaleTo(0.94, 60, Easing.CubicIn);
        await MasterRunButton.ScaleTo(1.0, 100, Easing.CubicOut);

        bool runReady = await SessionRefreshHelper.RefreshAndGetRunReadyAsync(_sessionService);
        UpdateSessionIndicator(runReady);
        if (!runReady)
        {
            await Navigation.PushAsync(new LoginPage());
            return;
        }

        var friends = _settingsService.GetEnabledFriends();
        if (friends.Count == 0)
        {
            await DisplayAlert(Localization.T("Dash_NoFriends"), Localization.T("Dash_NoFriendsBody"), Localization.T("Common_OK"));
            return;
        }

        var confirm = await DisplayAlert(
            Localization.T("Dash_RunNow"),
            string.Format(Localization.T(friends.Count == 1 ? "Dash_ConfirmRunOne" : "Dash_ConfirmRunMany"), friends.Count),
            Localization.T("Dash_Run"), Localization.T("Common_Cancel"));
        if (!confirm) return;

        _messageDraft = MessageEditor.Text ?? _messageDraft;
        PersistMessageText();

#if ANDROID
        bool permissionGranted = await RequestNotificationPermission();
        if (!permissionGranted) return;

        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        bool started = TiktokStreakSaver.Platforms.Android.StreakScheduler.RunNow(context);
        if (started)
        {
            await DisplayAlert(Localization.T("Dash_Started"), Localization.T("Dash_StartedBody"), Localization.T("Common_OK"));
            UpdateStatus();
        }
        else
        {
            await DisplayAlert(Localization.T("Dash_AlreadyRunning"), Localization.T("Dash_AlreadyRunningBody"), Localization.T("Common_OK"));
        }
#elif IOS
        await Platforms.iOS.Services.IosNotificationService.RequestPermissionAsync();

        var result = await Platforms.iOS.Services.IosStreakRunner.RunNowAsync(manual: true);
        await ShowIosRunResultAsync(result);
        UpdateStatus();
#else
        await DisplayAlert(Localization.T("Dash_Info"), Localization.T("Dash_AndroidOnly"), Localization.T("Common_OK"));
#endif
    }

    /// <summary>
    /// Runs only the chats that failed today, leaving the daily schedule and everyone else alone.
    /// </summary>
    private async void OnRetryFailedClicked(object? sender, EventArgs e)
    {
#if ANDROID
        // Read fresh rather than trusting the cached list: the button may have been left over from a
        // status refresh that happened before the user edited their friends.
        var failedIds = GetRetryableFailedFriendIds(
            _settingsService.GetRunHistory(), _settingsService.GetEnabledFriends());

        if (failedIds.Count == 0)
        {
            RetryFailedButton.IsVisible = false;
            await DisplayAlert(Localization.T("Dash_NothingToRetry"),
                Localization.T("Dash_NothingToRetryBody"), Localization.T("Common_OK"));
            return;
        }

        var confirm = await DisplayAlert(
            Localization.T("Dash_RetryFailed"),
            string.Format(Localization.T(failedIds.Count == 1 ? "Dash_ConfirmRetryOne" : "Dash_ConfirmRetryMany"), failedIds.Count),
            Localization.T("Dash_Retry"), Localization.T("Common_Cancel"));
        if (!confirm) return;

        bool permissionGranted = await RequestNotificationPermission();
        if (!permissionGranted) return;

        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        bool started = TiktokStreakSaver.Platforms.Android.StreakScheduler.RunFailedTargets(context, failedIds);

        if (started)
        {
            await DisplayAlert(Localization.T("Dash_RetryStarted"),
                Localization.T("Dash_RetryStartedBody"), Localization.T("Common_OK"));
            UpdateStatus();
        }
        else
        {
            await DisplayAlert(Localization.T("Dash_AlreadyRunning"),
                Localization.T("Dash_AlreadyRunningBody"), Localization.T("Common_OK"));
        }
#else
        await DisplayAlert(Localization.T("Dash_Info"), Localization.T("Dash_AndroidOnly"), Localization.T("Common_OK"));
#endif
    }

    private async void OnStopServiceClicked(object? sender, EventArgs e)
    {
        await StopServiceButton.ScaleTo(0.94, 60, Easing.CubicIn);
        await StopServiceButton.ScaleTo(1.0, 100, Easing.CubicOut);
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        TiktokStreakSaver.Platforms.Android.StreakScheduler.StopService(context);
#endif
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        GreetingLabel.Text = string.Format(Localization.T("Dash_Greeting"), _sessionService.GetDisplayName());
        LoadProfilePhoto();
        await RefreshSessionUiAsync();
        LoadSettings();
        UpdateStatus();
        await EvaluatePermissionsAsync();
#if ANDROID
        await CheckUpdateOnlyAsync();
#endif
        MainRefreshView.IsRefreshing = false;
    }

    private async Task EvaluatePermissionsAsync()
    {
#if IOS
        var authRequired = AppStorageProvider.Current.GetBool(AppConstants.AuthRequiredKey);
        if (authRequired && _sessionService.IsSessionValid())
        {
            PermissionsPanel.IsVisible = true;
            BtnNotification.IsVisible = false;
            BtnExactAlarm.IsVisible = false;
            BtnBatteryOpt.IsVisible = false;
        }
        else
        {
            PermissionsPanel.IsVisible = false;
        }
        await Platforms.iOS.Services.IosNotificationService.RequestPermissionAsync();
#elif ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        bool exactAlarmGranted = TiktokStreakSaver.Platforms.Android.StreakScheduler.CanScheduleExactAlarms(context);
        bool batteryOptGranted = TiktokStreakSaver.Platforms.Android.StreakScheduler.IsIgnoringBatteryOptimizations(context);
        bool notificationGranted = true;
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu)
        {
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
            notificationGranted = (status == PermissionStatus.Granted);
        }
        BtnExactAlarm.IsVisible = !exactAlarmGranted;
        BtnBatteryOpt.IsVisible = !batteryOptGranted;
        BtnNotification.IsVisible = !notificationGranted;
        PermissionsPanel.IsVisible = !exactAlarmGranted || !batteryOptGranted || !notificationGranted;
#else
        await Task.CompletedTask;
        PermissionsPanel.IsVisible = false;
#endif
    }

    private void OnRequestExactAlarmClicked(object? sender, EventArgs e)
    {
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        TiktokStreakSaver.Platforms.Android.StreakScheduler.RequestExactAlarmPermission(context);
#endif
    }

    private void OnRequestBatteryOptimizationClicked(object? sender, EventArgs e)
    {
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        TiktokStreakSaver.Platforms.Android.StreakScheduler.RequestBatteryOptimizationExemption(context);
#endif
    }

    private async void OnRequestNotificationClicked(object? sender, EventArgs e)
    {
        await RequestNotificationPermission();
        await EvaluatePermissionsAsync();
    }

    private async Task<bool> RequestNotificationPermission()
    {
#if ANDROID
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu)
        {
            var status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlert(Localization.T("Dash_PermissionRequired"), Localization.T("Dash_NotificationPermissionBody"), Localization.T("Common_OK"));
                return false;
            }
        }
#else
        await Task.CompletedTask;
#endif
        return true;
    }

#if IOS
    private async Task ShowIosRunResultAsync(Platforms.iOS.Services.IosRunResult result)
    {
        var (title, message) = result.Status switch
        {
            Platforms.iOS.Services.IosRunStatus.Completed =>
                (Localization.T("Ios_Done"), result.Message ?? Localization.T("Dash_IosRunFinished")),
            Platforms.iOS.Services.IosRunStatus.AlreadyRunning =>
                (Localization.T("Dash_AlreadyRunning"), result.Message ?? Localization.T("Dash_IosRunInProgress")),
            Platforms.iOS.Services.IosRunStatus.NoCookies =>
                (Localization.T("Dash_LoginRequired"), result.Message ?? Localization.T("Dash_SignInAgain")),
            Platforms.iOS.Services.IosRunStatus.Offline =>
                (Localization.T("Dash_Offline"), result.Message ?? Localization.T("Dash_NoNetwork")),
            Platforms.iOS.Services.IosRunStatus.NoFriendsDue =>
                (Localization.T("Dash_NoFriendsDue"), result.Message ?? Localization.T("Dash_NoFriendsDueBody")),
            Platforms.iOS.Services.IosRunStatus.TimedOut =>
                (Localization.T("Dash_TimedOut"), result.Message ?? Localization.T("Dash_TimedOutBody")),
            Platforms.iOS.Services.IosRunStatus.EngineMissing =>
                (Localization.T("Dash_Unavailable"), result.Message ?? Localization.T("Dash_EngineMissing")),
            _ =>
                (Localization.T("Dash_RunFailed"), result.Message ?? Localization.T("Dash_RunFailedBody"))
        };

        if ((result.Status is Platforms.iOS.Services.IosRunStatus.Failed
                or Platforms.iOS.Services.IosRunStatus.TimedOut)
            && !string.IsNullOrWhiteSpace(result.Details))
        {
            message = FormatIosRunAlertMessage(message, result.Details);
        }

        if (result.Status == Platforms.iOS.Services.IosRunStatus.NoCookies)
        {
            bool signIn = await DisplayAlert(title, message, Localization.T("Dash_SignIn"), Localization.T("Common_OK"));
            if (signIn)
                await Navigation.PushAsync(new LoginPage());
            return;
        }

        await DisplayAlert(title, message, Localization.T("Common_OK"));
    }

    private static string FormatIosRunAlertMessage(string primary, string details)
    {
        const int maxLength = 500;
        var combined = $"{primary}{Environment.NewLine}{Environment.NewLine}{details}";
        if (combined.Length <= maxLength)
            return combined;

        // The tail is measured rather than assumed, because its length depends on the language.
        var tail = Localization.T("Dash_SeeHistoryExportLogs");
        return combined[..(maxLength - tail.Length)] + tail;
    }
#endif
}
