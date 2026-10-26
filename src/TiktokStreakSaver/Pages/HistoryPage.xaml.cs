using Microsoft.Maui.Controls.Shapes;
using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using TiktokStreakSaver.Views;

namespace TiktokStreakSaver.Pages;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class HistoryPage : ContentPage
{
    private readonly SettingsService _settingsService;
    private readonly SuccessRateDrawable _chartDrawable;

    /// <summary>Refreshes the live entry while a run is in flight; idle otherwise.</summary>
    private IDispatcherTimer? _liveTimer;
    private bool _isExportingLogs = false;

    public HistoryPage()
    {
        InitializeComponent();
        _settingsService = new SettingsService();
        _chartDrawable = new SuccessRateDrawable();
        SuccessChartView.Drawable = _chartDrawable;
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
        this.Opacity = 0;
        this.TranslationY = 12;
        await Task.WhenAll(
            this.FadeTo(1, 280, Easing.SinInOut),
            this.TranslateTo(0, 0, 280, Easing.SinInOut));

        LoadStats();
        LoadHistory();

        // Ticks only while a run is actually in flight, so a finished page costs nothing. The check
        // is cheap (a lock and a count) and the page reloads only when the number of results moved.
        _liveTimer ??= Dispatcher.CreateTimer();
        _liveTimer.Interval = TimeSpan.FromSeconds(1);
        _liveTimer.Tick -= OnLiveTimerTick;
        _liveTimer.Tick += OnLiveTimerTick;
        _liveTimer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _liveTimer?.Stop();
        if (_liveTimer != null)
            _liveTimer.Tick -= OnLiveTimerTick;
    }

    private int _lastLiveCount = -1;

    private void OnLiveTimerTick(object? sender, EventArgs e)
    {
        var count = GetLiveResults().Count;

        // Nothing running and nothing shown: leave the page alone.
        if (count == 0 && _lastLiveCount <= 0)
            return;

        if (count == _lastLiveCount)
            return;

        _lastLiveCount = count;
        LoadHistory();
    }

    private void OnRefreshing(object? sender, EventArgs e)
    {
        LoadStats();
        LoadHistory();
        MainRefreshView.IsRefreshing = false;
    }

    private void LoadStats()
    {
        var history = _settingsService.GetRunHistory();

        int totalMessages = history.Sum(r => r.FriendResults?.Count ?? 0);
        int successMessages = history.Sum(r => r.FriendResults?.Count(f => f.Success) ?? 0);
        float rate = totalMessages > 0 ? (float)successMessages / totalMessages : 0;

        bool isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        _chartDrawable.IsDarkTheme = isDark;
        _chartDrawable.SuccessRate = rate;
        _chartDrawable.RateText = totalMessages > 0 ? $"{(int)(rate * 100)}%" : "—";
        _chartDrawable.SubText = totalMessages > 0 ? Localization.T("Hist_ChartSuccess") : "";
        SuccessChartView.Invalidate();

        if (history.Count > 0)
        {
            SuccessRateLabel.Text = totalMessages > 0
                ? string.Format(Localization.T("Hist_SummaryMessages"), successMessages, totalMessages)
                : string.Format(Localization.T("Hist_SummaryRuns"), history.Count(r => r.Success), history.Count);
            int tracked = Math.Min(history.Count, SettingsService.MaxRunHistoryEntries);
            var week = RunHistoryStats.ForLastDays(history, 7, DateTime.Today);
            TotalRunsLabel.Text = week.HasData
                ? string.Format(Localization.T("Hist_TrackedWeek"), tracked, week.SuccessPercent)
                : string.Format(Localization.T("Hist_Tracked"), tracked);

            // Same clock source as the Dashboard, so the two pages can never disagree about the last
            // run. History[0] is the newest entry, because AddRunResult inserts at the front.
            //
            // The line used to be derived from the run's recorded duration alone, which nothing ever
            // wrote, so a run that had plainly happened was reported as "Last run: N/A".
            var newestRun = history.FirstOrDefault();
            LastRunSummaryLabel.Text = RunRecency.DescribeLastRunWithDuration(
                _settingsService.GetLastRunTime(),
                newestRun?.Duration,
                DateTime.Now);
        }
        else
        {
            SuccessRateLabel.Text = Localization.T("Hist_NoData");
            TotalRunsLabel.Text = "";
            LastRunSummaryLabel.Text = "";
        }
    }

    private void LoadHistory()
    {
        var allHistory = _settingsService.GetRunHistory();

        // The run in flight is not in history yet, so it is prepended as a live entry. That is what
        // lets the page fill in chat by chat instead of showing nothing until the run is over.
        var liveResults = GetLiveResults();
        var itemsToRemove = HistoryContainer.Children.Where(c => c != NoHistoryLabel).ToList();
        foreach (var item in itemsToRemove) HistoryContainer.Children.Remove(item);

        NoHistoryLabel.IsVisible = allHistory.Count == 0 && liveResults.Count == 0;

        if (liveResults.Count > 0)
        {
            HistoryContainer.Children.Add(CreateHistoryView(new StreakRunResult
            {
                RunTime = DateTime.Now,
                DryRun = liveResults.All(r => r.DryRun),
                FriendResults = liveResults,
            }));
        }

        foreach (var run in allHistory) HistoryContainer.Children.Add(CreateHistoryView(run));
    }

    /// <summary>
    /// Per-chat results of the run currently in flight, or an empty list when nothing is running.
    /// </summary>
    private static List<FriendMessageResult> GetLiveResults()
    {
#if ANDROID
        return TiktokStreakSaver.Platforms.Android.Services.StreakService.LiveRunResults();
#else
        return new List<FriendMessageResult>();
#endif
    }

    private static string ShortenErrorMessage(string? originalMsg)
    {
        if (string.IsNullOrEmpty(originalMsg)) return "";

        // The run-level reasons the app itself writes (login wall, navigation, network, stop, WebView) are
        // already stored in the interface language, so the translated form matches and comes back as-is.
        // What RunFailureReason adds is the per-chat reasons the automation script reports in English, which
        // used to reach this list untranslated.
        if (Matches(originalMsg, "login required", "Hist_ErrorLoginRequired")) return Localization.T("Hist_ErrorLoginRequired");
        if (Matches(originalMsg, "navigate to", "Hist_ErrorNavigation")) return Localization.T("Hist_ErrorNavigation");
        if (Matches(originalMsg, "No network", "Hist_ErrorNoNetwork")) return Localization.T("Hist_ErrorNoNetwork");
        if (Matches(originalMsg, "stopped by user", "Hist_ErrorStopped")) return Localization.T("Hist_ErrorStopped");
        if (Matches(originalMsg, "WebView", "Hist_ErrorWebView")) return Localization.T("Hist_ErrorWebView");

        return RunFailureReason.Describe(originalMsg);
    }

    private static bool Matches(string message, string englishFragment, string key)
        => message.Contains(englishFragment, StringComparison.OrdinalIgnoreCase)
           || message.Contains(Localization.T(key), StringComparison.OrdinalIgnoreCase);

    private View CreateHistoryView(StreakRunResult run)
    {
        // A rehearsal gets its own colour: a green dot would claim a delivery that never happened.
        //
        // A run whose sends were not confirmed gets amber rather than green. Green used to mean only that
        // the script reported success, so a run where nothing could be found in the chats looked identical
        // to a fully delivered one.
        var (attempted, confirmed, unverified, _) = SendConfirmationSummary.Summarize(run.FriendResults);

        var statusColor = run.DryRun
            ? Color.FromArgb("#F59E0B")
            : !run.Success ? GetThemeColor("Error", "#9C2121")
            : attempted > 0 && confirmed < attempted ? Color.FromArgb("#F59E0B")
            : Color.FromArgb("#22C55E");
        var border = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Padding = new Thickness(14, 12),
            Margin = new Thickness(0, 4)
        };
        border.SetAppThemeColor(Border.BackgroundColorProperty, GetThemeColor("Gray100", "#F3F4F6"), GetThemeColor("Gray900", "#111827"));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star },
            },
            ColumnSpacing = 12,
            VerticalOptions = LayoutOptions.Center
        };
        var statusDot = new Border
        {
            WidthRequest = 8,
            HeightRequest = 8,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            BackgroundColor = statusColor,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(4, 0)
        };
        grid.Children.Add(statusDot);
        var infoStack = new VerticalStackLayout { Spacing = 3 };
        infoStack.Children.Add(new Label { Text = run.RunTime.ToString("MMM dd, HH:mm"), FontSize = 15, FontFamily = "InterMedium" });

        if (run.DryRun)
        {
            var testLabel = new Label
            {
                Text = Localization.T("Hist_TestRun"),
                FontSize = 13,
                FontFamily = "InterMedium",
            };
            testLabel.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb("#B45309"), Color.FromArgb("#F59E0B"));
            infoStack.Children.Add(testLabel);
        }
        else
        {
            // Only surfaced when it matters: an unverified send is the difference between "we think it
            // went out" and "we know it did". The counts come from the top of this method, which the dot
            // colour already needs.
            if (unverified > 0 && attempted > 0)
            {
                var verifyLabel = new Label
                {
                    Text = string.Format(Localization.T("Hist_ConfirmedUnverified"), confirmed, attempted, unverified),
                    FontSize = 13,
                };
                verifyLabel.SetAppThemeColor(Label.TextColorProperty,
                    Color.FromArgb("#B45309"), Color.FromArgb("#F59E0B"));
                infoStack.Children.Add(verifyLabel);
            }
        }

        var successCount = run.FriendResults.Count(r => r.Success);
        var totalCount = run.FriendResults.Count;
        if (totalCount > 0)
        {
            // Every entry that is not a success is a failure. This used to be labelled "skipped",
            // which read as "the run chose not to message them" and hid real failures behind a word
            // that sounds intentional.
            var failedCount = totalCount - successCount;
            if (failedCount > 0)
            {
                var hStack = new HorizontalStackLayout { Spacing = 16 };
                var l1 = new Label { Text = string.Format(Localization.T("Hist_SentCount"), successCount, totalCount), FontSize = 13 };
                l1.SetAppThemeColor(Label.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
                var l2 = new Label { Text = string.Format(Localization.T("Hist_FailedCount"), failedCount), FontSize = 13 };
                l2.SetAppThemeColor(Label.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
                hStack.Children.Add(l1);
                hStack.Children.Add(l2);
                infoStack.Children.Add(hStack);
            }
            else
            {
                var infoLabel = new Label { Text = string.Format(Localization.T("Hist_MessagesSent"), successCount, totalCount), FontSize = 13 };
                infoLabel.SetAppThemeColor(Label.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
                infoStack.Children.Add(infoLabel);
            }
        }
        else if (!string.IsNullOrEmpty(run.ErrorMessage))
        {
            var shortMsg = ShortenErrorMessage(run.ErrorMessage);
            infoStack.Children.Add(new Label { Text = shortMsg, FontSize = 12, TextColor = statusColor, LineBreakMode = LineBreakMode.TailTruncation });
        }

        // Tapping a run opens the per-chat breakdown. The summary above says how many landed; this is the
        // only way to find out which ones, and whether "sent" was actually confirmed or merely assumed.
        if (run.FriendResults.Count > 0)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var chevron = new Label
            {
                Text = "›",
                FontSize = 20,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.End,
            };
            chevron.SetAppThemeColor(Label.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => await ShowRunDetailAsync(run);
            border.GestureRecognizers.Add(tap);
        }

        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);
        border.Content = grid;
        return border;
    }

    /// <summary>
    /// The per-chat breakdown of one run: what was sent, to whom, and how sure the app is.
    /// <para>
    /// "Sent" in the list above means the script reported success. The distinction this page adds is
    /// whether that was <em>confirmed</em> - the message found in the thread - or only assumed because the
    /// composer cleared. Without it the user has no way to tell a real delivery from a hopeful one.
    /// </para>
    /// </summary>
    private async Task ShowRunDetailAsync(StreakRunResult run)
    {
        var results = run.FriendResults ?? new List<FriendMessageResult>();
        var lines = new List<string>();

        foreach (var result in results.OrderBy(r => r.Timestamp))
        {
            var name = string.IsNullOrWhiteSpace(result.Username) ? result.FriendId : "@" + result.Username;

            if (result.DryRun)
            {
                lines.Add($"· {name} — {Localization.T("Hist_DetailDryRun")}");
                continue;
            }

            if (!result.Success)
            {
                var reason = ShortenErrorMessage(result.ErrorMessage);
                lines.Add($"✕ {name} — {(string.IsNullOrEmpty(reason) ? Localization.T("Hist_DetailFailed") : reason)}");
                continue;
            }

            lines.Add(result.Verified switch
            {
                true => $"✓ {name} — {Localization.T("Hist_DetailConfirmed")}",
                false => $"! {name} — {Localization.T("Hist_DetailUnverified")}",
                _ => $"✓ {name} — {Localization.T("Hist_DetailSentUnchecked")}",
            });
        }

        if (lines.Count == 0)
            lines.Add(Localization.T("Hist_DetailNothing"));

        var (attempted, confirmed, unverified, _) = SendConfirmationSummary.Summarize(run.FriendResults);
        var header = attempted > 0
            ? string.Format(Localization.T("Hist_DetailHeader"), run.RunTime.ToString("MMM dd, HH:mm"), confirmed, attempted, unverified)
            : run.RunTime.ToString("MMM dd, HH:mm");

        await DisplayAlert(header, string.Join(Environment.NewLine, lines), Localization.T("Common_Close"));
    }

    private async void OnClearHistoryClicked(object? sender, EventArgs e)
    {
        var history = _settingsService.GetRunHistory();
        if (history.Count == 0 && !HasLogsToClear())
        {
            await DisplayAlert(Localization.T("Hist_NothingToClear"), Localization.T("Hist_NothingToClearBody"), Localization.T("Common_OK"));
            return;
        }

        bool confirm = await DisplayAlert(
            Localization.T("Hist_ClearTitle"),
            Localization.T("Hist_ClearBody"),
            Localization.T("Hist_Clear"),
            Localization.T("Common_Cancel"));
        if (!confirm)
            return;

        _settingsService.ClearRunHistory();
        ClearPlatformLogs();
        LoadHistory();
        LoadStats();
    }

    private static bool HasLogsToClear()
    {
#if ANDROID
        return TiktokStreakSaver.Platforms.Android.Services.StreakService.GetLogs().Count > 0;
#elif IOS
        return TiktokStreakSaver.Platforms.iOS.Services.IosRunLogStore.GetLogs().Count > 0;
#else
        return false;
#endif
    }

    private static void ClearPlatformLogs()
    {
#if ANDROID
        TiktokStreakSaver.Platforms.Android.Services.StreakService.ClearLogs();
#elif IOS
        TiktokStreakSaver.Platforms.iOS.Services.IosRunLogStore.Clear();
#endif
    }

    private async void OnExportLogsClicked(object? sender, EventArgs e)
    {
        if (_isExportingLogs) return;
        _isExportingLogs = true;
        try
        {
#if ANDROID
            var logs = TiktokStreakSaver.Platforms.Android.Services.StreakService.GetLogs();
#elif IOS
            var logs = TiktokStreakSaver.Platforms.iOS.Services.IosRunLogStore.GetLogs();
#else
            var logs = new List<string>();
#endif
            if (logs == null || logs.Count == 0)
            {
#if IOS
                var paths = string.Join(Environment.NewLine, TiktokStreakSaver.Platforms.iOS.Services.AppGroupPaths.GetAllRunLogsFilePaths());
                await DisplayAlert(
                    Localization.T("Hist_ExportLogs"),
                    string.Format(Localization.T("Hist_NoLogsExportPaths"), Environment.NewLine, Environment.NewLine + paths),
                    Localization.T("Common_OK"));
#else
                await DisplayAlert(Localization.T("Hist_ExportLogs"), Localization.T("Hist_NoLogsToExport"), Localization.T("Common_OK"));
#endif
                return;
            }
            var textContent = string.Join(Environment.NewLine, logs);
            var fileName = $"streak_logs_{DateTime.Now:yyyyMMdd_HHmm}.txt";
            var filePath = System.IO.Path.Combine(FileSystem.CacheDirectory, fileName);
            await System.IO.File.WriteAllTextAsync(filePath, textContent);
            await Share.Default.RequestAsync(new ShareFileRequest { Title = Localization.T("Hist_ExportSystemLogs"), File = new ShareFile(filePath, "text/plain") });
        }
        catch (Exception ex) { await DisplayAlert(Localization.T("Hist_ExportFailed"), string.Format(Localization.T("Hist_ExportFailedBody"), ex.Message), Localization.T("Common_OK")); }
        finally { _isExportingLogs = false; }
    }
}
