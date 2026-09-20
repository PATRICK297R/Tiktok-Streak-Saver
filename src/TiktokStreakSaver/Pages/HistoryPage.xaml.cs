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
        var itemsToRemove = HistoryContainer.Children.Where(c => c != NoHistoryLabel).ToList();
        foreach (var item in itemsToRemove) HistoryContainer.Children.Remove(item);
        NoHistoryLabel.IsVisible = allHistory.Count == 0;
        foreach (var run in allHistory) HistoryContainer.Children.Add(CreateHistoryView(run));
    }

    private string ShortenErrorMessage(string? originalMsg)
    {
        if (string.IsNullOrEmpty(originalMsg)) return "";
        // A stored error may have been written in either language, so the English wording and the translated
        // short form are both matched before the message is replaced with the short form.
        if (Matches(originalMsg, "login required", "Hist_ErrorLoginRequired")) return Localization.T("Hist_ErrorLoginRequired");
        if (Matches(originalMsg, "navigate to", "Hist_ErrorNavigation")) return Localization.T("Hist_ErrorNavigation");
        if (Matches(originalMsg, "No network", "Hist_ErrorNoNetwork")) return Localization.T("Hist_ErrorNoNetwork");
        if (Matches(originalMsg, "stopped by user", "Hist_ErrorStopped")) return Localization.T("Hist_ErrorStopped");
        if (Matches(originalMsg, "WebView", "Hist_ErrorWebView")) return Localization.T("Hist_ErrorWebView");
        return originalMsg;
    }

    private static bool Matches(string message, string englishFragment, string key)
        => message.Contains(englishFragment, StringComparison.OrdinalIgnoreCase)
           || message.Contains(Localization.T(key), StringComparison.OrdinalIgnoreCase);

    private View CreateHistoryView(StreakRunResult run)
    {
        // A rehearsal gets its own colour: a green dot would claim a delivery that never happened.
        var statusColor = run.DryRun
            ? Color.FromArgb("#F59E0B")
            : run.Success ? Color.FromArgb("#22C55E") : GetThemeColor("Error", "#9C2121");
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
            // went out" and "we know it did".
            var (attempted, confirmed, unverified, _) = SendConfirmationSummary.Summarize(run.FriendResults);
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
            var skippedCount = totalCount - successCount;
            if (skippedCount > 0)
            {
                var hStack = new HorizontalStackLayout { Spacing = 16 };
                var l1 = new Label { Text = string.Format(Localization.T("Hist_SentCount"), successCount, totalCount), FontSize = 13 };
                l1.SetAppThemeColor(Label.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
                var l2 = new Label { Text = string.Format(Localization.T("Hist_SkippedCount"), skippedCount), FontSize = 13 };
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

        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);
        border.Content = grid;
        return border;
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
