using Microsoft.Maui.Controls.Shapes;
using TiktokStreakSaver.Services;

namespace TiktokStreakSaver.Pages;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class ProfilePage : ContentPage
{
    private readonly SessionService _sessionService;
    private readonly SettingsService _settingsService;
    private readonly BackupService _backupService;
    private bool _suppressIntervalChanged = false;
    private bool _suppressRandomizeToggle = false;
    private bool _suppressScheduleTimeChanged = false;
    private bool _suppressStreakRiskTimeChanged = false;
    /// <summary>Set while the language picker is being filled, so selecting an item does not look like a change.</summary>
    private bool _suppressLanguageChanged = false;
#if ANDROID
    private readonly UpdateService _updateService;
    private bool _isCheckingUpdates = false;
#endif

    public ProfilePage()
    {
        InitializeComponent();
        _sessionService = new SessionService();
        _settingsService = new SettingsService();
        _backupService = new BackupService(_settingsService, _sessionService);
#if ANDROID
        _updateService = new UpdateService();
#endif
        BuildLanguagePicker();
    }

    /// <summary>
    /// Fills the language picker and selects the language in use.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="AppLanguage"/> rather than written into the XAML, so the two options and the
    /// code that reads the selection cannot drift apart. The guard is needed because setting
    /// <c>SelectedIndex</c> raises the change handler, and that would write the same language back.
    /// </remarks>
    private void BuildLanguagePicker()
    {
        _suppressLanguageChanged = true;

        LanguagePicker.ItemsSource = new List<string>
        {
            Localization.T("Prof_LanguageEnglish"),
            Localization.T("Prof_LanguageIndonesian")
        };

        LanguagePicker.SelectedIndex = Localization.Current.Language == AppLanguage.Indonesian ? 1 : 0;

        _suppressLanguageChanged = false;
    }

    /// <summary>
    /// Applies the chosen language.
    /// </summary>
    /// <remarks>
    /// The visible screen redraws by itself: every translated label is bound to the localization indexer,
    /// which raises a change notification. What is left here is the text this page set in code - the
    /// greeting, the schedule summary, the version line - so the page is reloaded from storage to repaint
    /// those as well.
    /// </remarks>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_suppressLanguageChanged)
            return;

        var chosen = LanguagePicker.SelectedIndex == 1 ? AppLanguage.Indonesian : AppLanguage.English;
        if (chosen == Localization.Current.Language)
            return;

        Localization.Current.SetLanguage(chosen);

        // The picker's own two options are language names, so they are rebuilt in the new language.
        BuildLanguagePicker();

        LoadSettingsFromStorage();
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
            await UpdateLoginButtonStateAsync());
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
        SessionState.Changed -= OnSessionStateChanged;
        SessionState.Changed += OnSessionStateChanged;

        this.Opacity = 0;
        this.TranslationY = 12;
        await Task.WhenAll(
            this.FadeTo(1, 280, Easing.SinInOut),
            this.TranslateTo(0, 0, 280, Easing.SinInOut));

        LoadSettingsFromStorage();

        await UpdateLoginButtonStateAsync();
    }

    /// <summary>
    /// Reads every stored setting back into the controls.
    /// <para>
    /// Extracted from <see cref="OnAppearing"/> so a restore can repaint the page with the values it
    /// just wrote, instead of leaving the pre-restore toggles and times on screen.
    /// </para>
    /// </summary>
    private void LoadSettingsFromStorage()
    {
        LoadProfilePhoto();

        DisplayNameEntry.Text = _sessionService.GetDisplayName();

        ScheduleSwitch.IsToggled = _settingsService.IsScheduled();
        SkipUnreachableSwitch.IsToggled = _settingsService.GetSkipUnreachableUsers();
        _suppressRandomizeToggle = true;
        try
        {
            RandomizeMessagesSwitch.IsToggled = _settingsService.GetRandomizeNormalMessages();
        }
        finally
        {
            _suppressRandomizeToggle = false;
        }
        SendOnBatteryLowSwitch.IsToggled = _settingsService.GetSendOnBatteryLow();

        StreakRiskSwitch.IsToggled = _settingsService.GetStreakRiskAlerts();
        StreakRiskTimeRow.IsVisible = StreakRiskSwitch.IsToggled;
        DryRunSwitch.IsToggled = _settingsService.GetDryRun();

        _suppressStreakRiskTimeChanged = true;
        try
        {
            var warningHour = _settingsService.GetStreakRiskWarningHour();
            StreakRiskTimePicker.Time = new TimeSpan(warningHour, 0, 0);
        }
        finally
        {
            _suppressStreakRiskTimeChanged = false;
        }

        FixedTimeSwitch.IsToggled = _settingsService.GetUseFixedTime();

        // Guarded: assigning Time raises PropertyChanged, and the handler persists the schedule.
        // Loading the page must not rewrite settings as a side effect.
        _suppressScheduleTimeChanged = true;
        try
        {
            ScheduleTimePicker.Time = new TimeSpan(
                _settingsService.GetFixedTimeHour(),
                _settingsService.GetFixedTimeMinute(), 0);
        }
        finally
        {
            _suppressScheduleTimeChanged = false;
        }

        ScheduleOptionsPanel.IsVisible = ScheduleSwitch.IsToggled;
        TimePickerRow.IsVisible = FixedTimeSwitch.IsToggled;
        IntervalPanel.IsVisible = !FixedTimeSwitch.IsToggled;
        RefreshScheduleTimesList();

        _suppressIntervalChanged = true;
        try
        {
            var hours = _settingsService.GetIntervalHours();
            IntervalHoursStepper.Value = hours;
            UpdateIntervalLabels();
        }
        finally
        {
            _suppressIntervalChanged = false;
        }

        VersionLabel.Text = $"v{AppInfo.Current.VersionString}";

#if IOS
        IosShortcutsSettingsPanel.IsVisible = true;
        AndroidSchedulingPanel.IsVisible = false;
        CheckUpdatesButton.IsVisible = false;
        AltStoreUpdatesLabel.IsVisible = true;
#else
        IosShortcutsSettingsPanel.IsVisible = false;
        AndroidSchedulingPanel.IsVisible = true;
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SessionState.Changed -= OnSessionStateChanged;
    }

    private void LoadProfilePhoto()
    {
        var photoPath = _sessionService.GetProfileImagePath();
        if (!string.IsNullOrEmpty(photoPath) && System.IO.File.Exists(photoPath))
        {
            ProfilePhoto.Source = ImageSource.FromFile(photoPath);
            ProfilePhoto.IsVisible = true;
            ProfileEmoji.IsVisible = false;
            ProfilePhoto.Clip = new EllipseGeometry
            {
                Center = new Point(28, 28),
                RadiusX = 28,
                RadiusY = 28
            };
        }
        else
        {
            ProfilePhoto.IsVisible = false;
            ProfileEmoji.IsVisible = true;
        }
    }

    private async void OnProfilePhotoTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            var result = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions
            {
                Title = Localization.T("Prof_PickPhoto")
            });

            if (result != null)
            {
                var newFile = System.IO.Path.Combine(FileSystem.AppDataDirectory, result.FileName);
                var oldPath = _sessionService.GetProfileImagePath();
                if (!string.IsNullOrEmpty(oldPath) && oldPath != newFile && System.IO.File.Exists(oldPath))
                {
                    try { System.IO.File.Delete(oldPath); } catch { }
                }

                using (var stream = await result.OpenReadAsync())
                using (var newStream = System.IO.File.Create(newFile))
                    await stream.CopyToAsync(newStream);

                _sessionService.SetProfileImagePath(newFile);
                LoadProfilePhoto();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert(Localization.T("Prof_PhotoTitle"),
                string.Format(Localization.T("Prof_CouldNotPickPhoto"), ex.Message),
                Localization.T("Common_OK"));
        }
    }

    private void OnDisplayNameChanged(object? sender, EventArgs e)
    {
        _sessionService.SetDisplayName(DisplayNameEntry.Text ?? "User");
    }

    private async Task UpdateLoginButtonStateAsync()
    {
        bool runReady = await SessionRefreshHelper.RefreshAndGetRunReadyAsync(_sessionService);
        await ApplyLoginButtonStateAsync(runReady);
    }

    private async Task ApplyLoginButtonStateAsync(bool isSessionValid)
    {
        await LoginButton.FadeTo(0.5, 100);

        if (isSessionValid)
        {
            LoginButton.Text = Localization.T("Prof_SessionOk");
            LoginButton.BackgroundColor = GetThemeColor("Success", "#22946E");
            LoginButton.IsEnabled = false;
            SessionDot.BackgroundColor = GetThemeColor("Success", "#22946E");
            SessionStatusLabel.Text = Localization.T("Prof_SessionActive");
            var lastCheck = _sessionService.GetLastCheckTime();
            SessionLastCheckLabel.Text = lastCheck.HasValue
                ? string.Format(Localization.T("Prof_Verified"), lastCheck.Value.ToString("MMM dd, HH:mm"))
                : "";
        }
        else
        {
            LoginButton.Text = Localization.T("Prof_LoginToTikTok");
            LoginButton.BackgroundColor = GetThemeColor("Primary", "#FE2C55");
            LoginButton.IsEnabled = true;
            SessionDot.BackgroundColor = GetThemeColor("Error", "#9C2121");
            SessionStatusLabel.Text = Localization.T("Prof_NotLoggedIn");
            SessionLastCheckLabel.Text = Localization.T("Prof_TapBelowToLogin");
        }

        await LoginButton.FadeTo(1.0, 200);
    }

    private async void UpdateLoginButtonState(bool isSessionValid)
    {
        await ApplyLoginButtonStateAsync(isSessionValid);
    }

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new LoginPage());
        await UpdateLoginButtonStateAsync();
    }

    private async void OnShortcutTutorialClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new IosShortcutTutorialPage());
    }

    private async void OnIosLimitationsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new IosLimitationsPage());
    }

    private void OnScheduleToggled(object? sender, ToggledEventArgs e)
    {
        ScheduleOptionsPanel.IsVisible = e.Value;
        _settingsService.SetScheduled(e.Value);
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        if (e.Value)
            TiktokStreakSaver.Platforms.Android.StreakScheduler.ScheduleNextRun(context);
        else
            TiktokStreakSaver.Platforms.Android.StreakScheduler.CancelSchedule(context);
#endif
    }

    private void OnFixedTimeToggled(object? sender, ToggledEventArgs e)
    {
        _settingsService.SetUseFixedTime(e.Value);
        TimePickerRow.IsVisible = e.Value;
        IntervalPanel.IsVisible = !e.Value;
#if ANDROID
        if (_settingsService.IsScheduled())
        {
            var context = Platform.CurrentActivity ?? Android.App.Application.Context;
            TiktokStreakSaver.Platforms.Android.StreakScheduler.ScheduleNextRun(context);
        }
#endif
    }

    private void OnTimePickerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TimePicker.Time)) return;
        if (_suppressScheduleTimeChanged) return;

        // The picker edits the first entry. Any extra times are managed through the list below.
        var picked = ScheduleTimePicker.Time;
        var times = _settingsService.GetFixedDailyTimes();
        times[0] = new TimeSpan(picked.Hours, picked.Minutes, 0);

        ApplyScheduleTimes(times);
    }

    private void OnAddScheduleTimeClicked(object? sender, EventArgs e)
    {
        var times = _settingsService.GetFixedDailyTimes();

        // Always add a time that is not in use yet, so pressing this has a visible effect every time.
        // Reading the value from the "Run at" picker would not work: that picker edits the first entry,
        // so whatever it shows is already in the list.
        var startHour = times.Count > 0 ? times[0].Hours + 1 : 0;
        var next = ScheduleCalculator.NextFreeHour(times, startHour);

        if (next is null)
        {
            ScheduleTimesHint.Text = Localization.T("Prof_AllHoursScheduled");
            return;
        }

        times.Add(next.Value);
        ApplyScheduleTimes(times);
    }

    /// <summary>
    /// Persists the daily run times, refreshes the list and re-arms the alarm.
    /// </summary>
    private void ApplyScheduleTimes(List<TimeSpan> times)
    {
        _settingsService.SetFixedDailyTimes(times);
        RefreshScheduleTimesList();
        RescheduleForFixedDailyTimes();
    }

    /// <summary>
    /// Rebuilds the rows beneath the picker. Row 0 is the time shown in the picker, so it is only
    /// labelled; the remaining rows offer a remove control. The last entry can never be removed,
    /// because a daily schedule with no time has nothing to arm.
    /// </summary>
    private void RefreshScheduleTimesList()
    {
        var times = _settingsService.GetFixedDailyTimes();

        ScheduleTimesList.Children.Clear();
        ScheduleTimesHint.Text = Localization.T("Prof_ScheduleTimesHint");

        // Only the extra times are listed: the first one is edited by the "Run at" picker above.
        // Each row carries its own picker so editing an existing entry is unambiguous - the previous
        // design reused the one picker for both editing and adding, which meant "add" could only ever
        // produce a time that was already in the list.
        for (var i = 1; i < times.Count; i++)
        {
            var index = i;

            var label = new Label
            {
                Text = Localization.T("Prof_AlsoAt"),
                FontFamily = "InterMedium",
                FontSize = 14,
                VerticalOptions = LayoutOptions.Center,
            };

            var picker = new TimePicker
            {
                Time = times[i],
                Format = "HH:mm",
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.End,
                WidthRequest = 110,
            };

            // Subscribed after Time is set, so building the row cannot look like a user edit.
            picker.PropertyChanged += (_, args) => OnExtraScheduleTimeChanged(index, picker, args);

            var remove = new Button
            {
                Text = "\u2715",
                FontSize = 13,
                Padding = 0,
                WidthRequest = 34,
                HeightRequest = 34,
                CornerRadius = 17,
                BackgroundColor = Colors.Transparent,
                TextColor = Application.Current?.RequestedTheme == AppTheme.Dark
                    ? Color.FromArgb("#FF6B7F")
                    : Color.FromArgb("#FE2C55"),
                VerticalOptions = LayoutOptions.Center,
            };

            remove.Clicked += (_, _) => OnRemoveScheduleTimeClicked(index);

            var row = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                },
            };

            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            Grid.SetColumn(picker, 1);
            row.Children.Add(picker);

            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);

            ScheduleTimesList.Children.Add(row);
        }
    }

    /// <summary>
    /// Saves an edit to one of the extra run times. The list is deliberately not rebuilt here: doing so
    /// would destroy the picker the user is still interacting with.
    /// </summary>
    private void OnExtraScheduleTimeChanged(int index, TimePicker picker, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TimePicker.Time)) return;
        if (_suppressScheduleTimeChanged) return;

        var times = _settingsService.GetFixedDailyTimes();

        if (index <= 0 || index >= times.Count)
            return;

        var candidate = new TimeSpan(picker.Time.Hours, picker.Time.Minutes, 0);

        if (times[index] == candidate)
            return;

        times[index] = candidate;

        _settingsService.SetFixedDailyTimes(times);
        RescheduleForFixedDailyTimes();
    }

    private void OnRemoveScheduleTimeClicked(int index)
    {
        var times = _settingsService.GetFixedDailyTimes();

        if (index <= 0 || index >= times.Count)
            return;

        times.RemoveAt(index);

        if (times.Count == 0)
            return;

        ApplyScheduleTimes(times);
    }

    private void RescheduleForFixedDailyTimes()
    {
#if ANDROID
        if (_settingsService.IsScheduled() && _settingsService.GetUseFixedTime())
        {
            var context = Platform.CurrentActivity ?? Android.App.Application.Context;
            TiktokStreakSaver.Platforms.Android.StreakScheduler.ScheduleNextRun(context);
        }
#endif
    }

    private void OnIntervalHoursChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_suppressIntervalChanged) return;
        var hours = (int)Math.Round(e.NewValue);
        _settingsService.SetIntervalHours(hours);
        UpdateIntervalLabels();
#if ANDROID
        if (_settingsService.IsScheduled())
        {
            var ctx = Platform.CurrentActivity ?? Android.App.Application.Context;
            TiktokStreakSaver.Platforms.Android.StreakScheduler.ScheduleNextRun(ctx);
        }
#endif
    }

    private void UpdateIntervalLabels()
    {
        var hours = _settingsService.GetIntervalHours();
        IntervalHoursValueLabel.Text = hours.ToString();
        IntervalSummaryLabel.Text = hours == 1
            ? Localization.T("Prof_IntervalHour")
            : string.Format(Localization.T("Prof_IntervalSummary"), hours);
    }

    private void OnSkipUnreachableToggled(object? sender, ToggledEventArgs e)
    {
        _settingsService.SetSkipUnreachableUsers(e.Value);
    }

    private void OnRandomizeMessagesToggled(object? sender, ToggledEventArgs e)
    {
        if (_suppressRandomizeToggle)
            return;

        _settingsService.SetRandomizeNormalMessages(e.Value);
        SessionState.NotifyChanged();
    }

    private void OnSendOnBatteryLowToggled(object? sender, ToggledEventArgs e)
    {
        _settingsService.SetSendOnBatteryLow(e.Value);
    }

    private void OnStreakRiskToggled(object? sender, ToggledEventArgs e)
    {
        _settingsService.SetStreakRiskAlerts(e.Value);
        StreakRiskTimeRow.IsVisible = e.Value;
        RescheduleStreakRisk();
    }

    private void OnDryRunToggled(object? sender, ToggledEventArgs e)
    {
        _settingsService.SetDryRun(e.Value);
    }

    private void OnStreakRiskTimeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TimePicker.Time)) return;
        if (_suppressStreakRiskTimeChanged) return;

        _settingsService.SetStreakRiskWarningHour(StreakRiskTimePicker.Time.Hours);
        RescheduleStreakRisk();
    }

    /// <summary>
    /// Re-arms the daily risk check so a change takes effect immediately instead of at the next reboot.
    /// </summary>
    private void RescheduleStreakRisk()
    {
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        TiktokStreakSaver.Platforms.Android.StreakRiskScheduler.ScheduleNext(context);
#endif
    }

    private async void OnAboutClicked(object? sender, EventArgs e)
    {
        string currentVersion = AppInfo.Current.VersionString;
        await Navigation.PushModalAsync(new AboutPopupPage(
            Localization.T("Prof_AboutStreakSaver"), currentVersion, string.Empty, false));
    }

#if ANDROID
    private async void OnCheckUpdatesClicked(object? sender, EventArgs e)
    {
        if (_isCheckingUpdates) return;
        _isCheckingUpdates = true;
        var originalText = CheckUpdatesButton.Text;
        try
        {
            CheckUpdatesButton.IsEnabled = false;
            CheckUpdatesButton.Text = Localization.T("Prof_CheckingUpdates");

            var info = await _updateService.CheckForUpdatesAsync();
            if (info == null)
            {
                await DisplayAlert(Localization.T("Prof_UpdateCheckTitle"),
                    Localization.T("Prof_UpdateServerUnreachable"), Localization.T("Common_OK"));
                return;
            }

            string currentVersion = AppInfo.Current.VersionString;
            string remoteVersion = info.LatestVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? info.LatestVersion.Substring(1)
                : info.LatestVersion;

            if (info.HasUpdate)
            {
                await Navigation.PushModalAsync(new AboutPopupPage(
                    Localization.T("Dash_UpdateAvailable"), remoteVersion, info.Changelog, true, info.ApkDownloadUrl));
            }
            else
            {
                await DisplayAlert(Localization.T("Prof_UpToDate"),
                    string.Format(Localization.T("Prof_AlreadyLatest"), currentVersion), Localization.T("Common_OK"));
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert(Localization.T("Prof_UpdateCheckTitle"),
                string.Format(Localization.T("Prof_UpdateCheckFailed"), ex.Message), Localization.T("Common_OK"));
        }
        finally
        {
            CheckUpdatesButton.Text = originalText;
            CheckUpdatesButton.IsEnabled = true;
            _isCheckingUpdates = false;
        }
    }
#else
    private void OnCheckUpdatesClicked(object? sender, EventArgs e) { }
#endif

    private async void OnPrivacyPolicyClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new PrivacyPage());
    }

    private async void OnLogoutClicked(object? sender, EventArgs e)
    {
        bool confirm = await DisplayAlert(Localization.T("Prof_LogoutTitle"),
            Localization.T("Prof_LogoutBody"), Localization.T("Prof_LogoutTitle"), Localization.T("Common_Cancel"));
        if (confirm)
        {
            _sessionService.ClearSession();
            UpdateLoginButtonState(false);
        }
    }

    // ─── Backup & Restore ────────────────────────────────────────────────────

    private async void OnBackupClicked(object? sender, EventArgs e)
    {
        try
        {
            var backup = _backupService.Create();
            var json = AppBackupCodec.Serialize(backup);

            var filePath = System.IO.Path.Combine(FileSystem.CacheDirectory, BackupService.SuggestedFileName(DateTime.Now));
            await System.IO.File.WriteAllTextAsync(filePath, json);

            ShowBackupResult(string.Format(Localization.T("Prof_BackupDone"), backup.Friends.Count),
                isError: false);

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = Localization.T("Prof_BackupShareTitle"),
                File = new ShareFile(filePath, "application/json")
            });
        }
        catch (Exception ex)
        {
            ShowBackupResult(string.Format(Localization.T("Prof_BackupFailed"), ex.Message), isError: true);
        }
    }

    private async void OnRestoreClicked(object? sender, EventArgs e)
    {
        try
        {
            var picked = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = Localization.T("Prof_SelectBackupFile"),
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.Android, new[] { "application/json", "*/*" } },
                    { DevicePlatform.iOS, new[] { "public.json" } },
                    { DevicePlatform.WinUI, new[] { ".json" } },
                    { DevicePlatform.macOS, new[] { "json" } },
                })
            });
            if (picked == null) return;

            string json;
            using (var stream = await picked.OpenReadAsync())
            using (var reader = new System.IO.StreamReader(stream))
                json = await reader.ReadToEndAsync();

            // Refused rather than half-applied when the file is unreadable or from a newer build.
            if (!AppBackupCodec.TryDeserialize(json, out var backup, out var codecError) || backup is null)
            {
                var message = codecError ?? Localization.T("Prof_FileUnreadable");
                ShowBackupResult(message, isError: true);
                await DisplayAlert(Localization.T("Prof_RestoreFailed"), message, Localization.T("Common_OK"));
                return;
            }

            var summary = AppBackupCodec.Summarize(backup);
            var contents = string.Format(Localization.T("Prof_BackupContents"),
                               summary.Friends, summary.Groups, summary.RunHistoryCount) + "\n"
                         + string.Format(Localization.T("Prof_BackupWritten"), summary.ExportedAtText)
                         + (string.IsNullOrWhiteSpace(summary.AppVersion)
                             ? "."
                             : " " + string.Format(Localization.T("Prof_BackupByApp"), summary.AppVersion));

            var mergeOption = Localization.T("Prof_RestoreMerge");
            var replaceOption = Localization.T("Prof_RestoreReplace");
            var cancelOption = Localization.T("Common_Cancel");

            var choice = await DisplayActionSheet(
                string.Format(Localization.T("Prof_RestoreHowToApply"), contents),
                cancelOption,
                null,
                mergeOption,
                replaceOption);

            if (string.IsNullOrEmpty(choice) || choice == cancelOption)
                return;

            // The prefix is taken from the localized option, so the comparison follows the language on screen.
            var replacePrefix = replaceOption.Split(' ')[0];
            await ApplyRestoreAsync(backup, replace: choice.StartsWith(replacePrefix, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            ShowBackupResult(string.Format(Localization.T("Prof_RestoreFailedBody"), ex.Message), isError: true);
        }
    }

    /// <summary>
    /// Applies a parsed backup and reports what it changed, in counts rather than a bare "done".
    /// </summary>
    private async Task ApplyRestoreAsync(TiktokStreakSaver.Models.AppBackup backup, bool replace)
    {
        if (replace)
        {
            var confirmed = await DisplayAlert(
                Localization.T("Prof_ReplaceConfirmTitle"),
                Localization.T("Prof_ReplaceConfirmBody"),
                Localization.T("Prof_Replace"),
                Localization.T("Common_Cancel"));
            if (!confirmed)
                return;
        }

        if (!_backupService.TryApply(backup, replace, out var applied, out var applyError))
        {
            var message = applyError ?? Localization.T("Prof_RestoreIncomplete");
            ShowBackupResult(message, isError: true);
            await DisplayAlert(Localization.T("Prof_RestoreFailed"), message, Localization.T("Common_OK"));
            return;
        }

        var detail = replace
            ? string.Format(Localization.T("Prof_RestoreReplaced"), applied.FriendsTotal)
            : string.Format(Localization.T("Prof_RestoreMerged"),
                applied.FriendsAdded, applied.FriendsSkipped, applied.FriendsTotal);

        detail += " " + (applied.HistoryRestored
            ? Localization.T("Prof_HistoryRestored")
            : Localization.T("Prof_HistoryKept"));

        // The restored values are on screen now, not the ones from before the restore.
        LoadSettingsFromStorage();
        UpdateLoginButtonState(_sessionService.IsSessionValid());

        ShowBackupResult(detail, isError: false);
        await DisplayAlert(Localization.T("Prof_RestoreComplete"),
            detail + "\n\n" + Localization.T("Prof_RestoreLoginNote"),
            Localization.T("Common_OK"));
    }

    private void ShowBackupResult(string message, bool isError)
    {
        BackupResultLabel.Text = message;
        BackupResultLabel.IsVisible = true;
        BackupResultLabel.SetAppThemeColor(Label.TextColorProperty,
            isError ? GetThemeColor("DeleteColor", "#EE1D52") : GetThemeColor("Gray600", "#5F636A"),
            isError ? Color.FromArgb("#F87171") : GetThemeColor("Gray400", "#8B8F96"));
    }
}
