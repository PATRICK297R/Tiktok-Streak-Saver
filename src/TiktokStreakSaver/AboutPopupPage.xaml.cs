using TiktokStreakSaver.Services;
using Microsoft.Maui.ApplicationModel;

namespace TiktokStreakSaver;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class AboutPopupPage : ContentPage
{
    private readonly string _assignedVersion;
    private readonly bool _isUpdate;
    private readonly string? _apkDownloadUrl;
    private bool _isClosing = false;
    private bool _isDownloading = false;
    private CancellationTokenSource? _downloadCts;
    private bool _isFallbackMode = false;

    public AboutPopupPage(string title, string version, string changelog, bool isUpdate, string? apkDownloadUrl = null)
    {
        InitializeComponent();

        _assignedVersion = version;
        _isUpdate = isUpdate;
        _apkDownloadUrl = apkDownloadUrl;

        HeaderTitle.Text = title;
        VersionLabel.Text = $"v{version}";

        if (isUpdate)
        {
            // Update popup: show changelog, hide the tagline subtitle
            SubHeaderLabel.IsVisible = false;
            if (string.IsNullOrWhiteSpace(changelog))
                ChangelogBorder.IsVisible = false;
            else
                LoadChangelogHtml(changelog);

            CloseButton.IsVisible = false;
            UpdateButtonGrid.IsVisible = true;
        }
        else
        {
            // Welcome popup: hide changelog entirely, show tagline subtitle
            ChangelogBorder.IsVisible = false;
            SubHeaderLabel.IsVisible = true;

            CloseButton.IsVisible = true;
            UpdateButtonGrid.IsVisible = false;
        }
    }

    private void LoadChangelogHtml(string markdown)
    {
        bool isDark = Application.Current?.RequestedTheme == AppTheme.Dark;

        // ── Color palette ──────────────────────────────────────────────────
        string bg        = isDark ? "#121212" : "#F2F2F2";
        string fg        = isDark ? "#FFFFFF" : "#000000";
        string fgMuted   = isDark ? "#92979E" : "#666666";
        string accent    = isDark ? "#FF6B7F" : "#FE2C55";
        string cardBg    = isDark ? "#282828" : "#FFFFFF";
        string divider   = isDark ? "#3F3F3F" : "#E1E8ED";
        string utilBg    = isDark ? "#1E1E1E" : "#F4F4F4";
        string bulletCol = isDark ? "#3F3F3F" : "#E1E8ED";

        string bodyHtml = ConvertMarkdownToStructuredHtml(markdown);

        string html = $@"<!DOCTYPE html>
<html><head><meta name='viewport' content='width=device-width,initial-scale=1,maximum-scale=1'>
<style>
*{{margin:0;padding:0;box-sizing:border-box;}}
body{{
  background:{bg};color:{fg};
  font-family:-apple-system,'SF Pro Text',Roboto,'Segoe UI',sans-serif;
  font-size:14px;line-height:1.6;
  padding:16px;
  -webkit-text-size-adjust:100%;
}}

/* ── Section cards ── */
.section{{
  margin-bottom:16px;
}}
.section:last-child{{margin-bottom:0;}}

.section-title{{
  font-size:15px;font-weight:700;
  color:{fg};
  margin:0 0 10px 0;
  padding-bottom:8px;
  border-bottom:1px solid {divider};
  letter-spacing:-0.2px;
}}

.section-body p{{
  margin:6px 0;
  color:{fgMuted};
  font-size:13px;
  line-height:1.55;
}}

/* ── Bullet lists ── */
.section-body ul,.section-body ol{{
  list-style:none;
  padding:0;margin:0;
}}
.section-body ul li{{
  position:relative;
  padding:8px 0 8px 18px;
  font-size:13.5px;
  line-height:1.5;
  border-bottom:1px solid {divider};
}}
.section-body ul li:last-child{{border-bottom:none;}}
.section-body ul li::before{{
  content:'';
  position:absolute;left:0;top:15px;
  width:6px;height:6px;
  border-radius:50%;
  background:{accent};
}}
.section-body ol{{
  counter-reset:step;
}}
.section-body ol li{{
  position:relative;
  padding:8px 0 8px 24px;
  font-size:13.5px;
  line-height:1.5;
  border-bottom:1px solid {divider};
  counter-increment:step;
}}
.section-body ol li:last-child{{border-bottom:none;}}
.section-body ol li::before{{
  content:counter(step);
  position:absolute;left:0;top:8px;
  width:18px;height:18px;
  border-radius:50%;
  background:{accent};
  color:#fff;
  font-size:11px;font-weight:700;
  display:flex;align-items:center;justify-content:center;
}}

/* ── Inline elements ── */
strong{{font-weight:600;color:{fg};}}
em{{font-style:italic;}}
code{{
  background:{divider};
  padding:1px 5px;
  border-radius:4px;
  font-size:12px;
  font-family:'SF Mono',Menlo,monospace;
}}

/* ── Utility sections (Installation, Requirements) ── */
.util-section{{
  background:{utilBg};
  border-radius:10px;
  padding:12px 14px;
  margin-bottom:10px;
}}
.util-section:last-child{{margin-bottom:0;}}
.util-title{{
  font-size:13px;font-weight:700;
  color:{fgMuted};
  text-transform:uppercase;
  letter-spacing:0.5px;
  margin:0 0 8px 0;
}}
.util-section ul li,.util-section ol li{{
  font-size:13px;
  color:{fgMuted};
  border-bottom-color:{divider};
}}
.util-section ul li::before{{
  background:{bulletCol};
}}
.util-section ol li::before{{
  background:{bulletCol};
}}

/* ── Divider ── */
.section-divider{{
  border:none;
  border-top:1px solid {divider};
  margin:4px 0;
}}

/* ── Blockquote ── */
blockquote{{
  border-left:3px solid {accent};
  padding:6px 12px;
  margin:8px 0;
  color:{fgMuted};
  font-size:13px;
  background:{utilBg};
  border-radius:0 6px 6px 0;
}}
</style></head><body>{bodyHtml}</body></html>";

        ChangelogWebView.Source = new HtmlWebViewSource { Html = html };

        // Auto-size WebView to content
        ChangelogWebView.Navigated += (s, e) =>
        {
            ChangelogWebView.EvaluateJavaScriptAsync("document.body.scrollHeight")
                .ContinueWith(t =>
                {
                    if (t.Result is string result && double.TryParse(result, out double height) && height > 0)
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            ChangelogWebView.HeightRequest = height + 16;
                        });
                    }
                });
        };
    }

    // ── Section-aware Markdown-to-HTML converter ────────────────────────────

    /// <summary>
    /// Utility sections (Installation, Requirements) get distinct styling.
    /// </summary>
    private static readonly HashSet<string> UtilitySections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Installation", "Requirements", "About"
    };

    /// <summary>
    /// Converts Markdown into structured HTML with section grouping.
    /// H2 and H3 headers start new sections; lists and paragraphs fill sections.
    /// The first H2 matching "What's new in this release" is stripped because
    /// the dialog header already shows the title and version.
    /// </summary>
    private static string ConvertMarkdownToStructuredHtml(string markdown)
    {
        var lines = markdown.Split('\n');
        var sb = new System.Text.StringBuilder();
        bool inSection = false;
        bool inSectionBody = false;
        bool inUl = false;
        bool inOl = false;
        bool firstH2Stripped = false;

        foreach (var rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

            // ── Horizontal rules → divider ──
            if (System.Text.RegularExpressions.Regex.IsMatch(line.Trim(), @"^-{3,}$|^\*{3,}$|^_{3,}$"))
            {
                CloseList(sb, ref inUl, ref inOl);
                CloseSectionBody(sb, ref inSectionBody);
                CloseSection(sb, ref inSection);
                sb.AppendLine("<hr class='section-divider'/>");
                continue;
            }

            // ── H2 headers → new top-level section ──
            if (line.StartsWith("## "))
            {
                string title = line[3..].Trim();

                // Strip "What's new in this release (vX.X.X)" — redundant with dialog header
                if (!firstH2Stripped && title.StartsWith("What", StringComparison.OrdinalIgnoreCase))
                {
                    firstH2Stripped = true;
                    continue;
                }

                CloseList(sb, ref inUl, ref inOl);
                CloseSectionBody(sb, ref inSectionBody);
                CloseSection(sb, ref inSection);

                bool isUtil = UtilitySections.Contains(title.Trim());
                sb.AppendLine(isUtil ? "<div class='section util-section'>" : "<div class='section'>");
                sb.AppendLine(isUtil
                    ? $"<div class='util-title'>{FormatInline(title)}</div>"
                    : $"<div class='section-title'>{FormatInline(title)}</div>");
                inSection = true;

                sb.AppendLine("<div class='section-body'>");
                inSectionBody = true;
                continue;
            }

            // ── H3 headers → new sub-section ──
            if (line.StartsWith("### "))
            {
                string title = line[4..].Trim();

                CloseList(sb, ref inUl, ref inOl);
                CloseSectionBody(sb, ref inSectionBody);
                CloseSection(sb, ref inSection);

                bool isUtil = UtilitySections.Contains(title.Trim());
                sb.AppendLine(isUtil ? "<div class='section util-section'>" : "<div class='section'>");
                sb.AppendLine(isUtil
                    ? $"<div class='util-title'>{FormatInline(title)}</div>"
                    : $"<div class='section-title'>{FormatInline(title)}</div>");
                inSection = true;

                sb.AppendLine("<div class='section-body'>");
                inSectionBody = true;
                continue;
            }

            // ── H1 headers (rare, treat as section title) ──
            if (line.StartsWith("# "))
            {
                CloseList(sb, ref inUl, ref inOl);
                CloseSectionBody(sb, ref inSectionBody);
                CloseSection(sb, ref inSection);
                // Skip — redundant top-level title
                continue;
            }

            // ── Ensure content is inside a section ──
            if (!inSection)
            {
                sb.AppendLine("<div class='section'>");
                inSection = true;
                sb.AppendLine("<div class='section-body'>");
                inSectionBody = true;
            }

            // ── Blockquote ──
            if (line.StartsWith("> "))
            {
                CloseList(sb, ref inUl, ref inOl);
                sb.AppendLine($"<blockquote>{FormatInline(line[2..])}</blockquote>");
                continue;
            }

            // ── Unordered list ──
            var ulMatch = System.Text.RegularExpressions.Regex.Match(line, @"^\s*[-*]\s+(.+)");
            if (ulMatch.Success)
            {
                if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                if (!inUl) { sb.AppendLine("<ul>"); inUl = true; }
                sb.AppendLine($"<li>{FormatInline(ulMatch.Groups[1].Value)}</li>");
                continue;
            }

            // ── Ordered list ──
            var olMatch = System.Text.RegularExpressions.Regex.Match(line, @"^\s*\d+\.\s+(.+)");
            if (olMatch.Success)
            {
                if (inUl) { sb.AppendLine("</ul>"); inUl = false; }
                if (!inOl) { sb.AppendLine("<ol>"); inOl = true; }
                sb.AppendLine($"<li>{FormatInline(olMatch.Groups[1].Value)}</li>");
                continue;
            }

            // ── Empty line ──
            if (string.IsNullOrWhiteSpace(line))
            {
                CloseList(sb, ref inUl, ref inOl);
                continue;
            }

            // ── Plain paragraph ──
            CloseList(sb, ref inUl, ref inOl);
            sb.AppendLine($"<p>{FormatInline(line)}</p>");
        }

        CloseList(sb, ref inUl, ref inOl);
        CloseSectionBody(sb, ref inSectionBody);
        CloseSection(sb, ref inSection);
        return sb.ToString();
    }

    private static void CloseList(System.Text.StringBuilder sb, ref bool inUl, ref bool inOl)
    {
        if (inUl) { sb.AppendLine("</ul>"); inUl = false; }
        if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
    }

    private static void CloseSectionBody(System.Text.StringBuilder sb, ref bool inSectionBody)
    {
        if (inSectionBody) { sb.AppendLine("</div>"); inSectionBody = false; }
    }

    private static void CloseSection(System.Text.StringBuilder sb, ref bool inSection)
    {
        if (inSection) { sb.AppendLine("</div>"); inSection = false; }
    }

    /// <summary>Formats inline Markdown: bold, italic, inline code.</summary>
    private static string FormatInline(string text)
    {
        // Inline code: `code`
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`]+)`", "<code>$1</code>");
        // Bold: **text** or __text__
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"__(.+?)__", "<strong>$1</strong>");
        // Italic: *text* or _text_
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*(.+?)\*", "<em>$1</em>");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<!\w)_(.+?)_(?!\w)", "<em>$1</em>");
        return text;
    }

    // ── Modal dismissal ────────────────────────────────────────────────────
    //
    // The popup is presented with Navigation.PushModalAsync from the page underneath, so it can end
    // up on either this page's navigation stack or Shell's, depending on which navigation proxy
    // handled the push. Popping only one of them leaves the popup on screen. Failures are reported
    // rather than swallowed so callers can leave their buttons usable instead of stranding the user
    // on a popup whose only action does nothing.
    //
    /// <summary>Closes this popup. Returns false when it could not be dismissed.</summary>
    private async Task<bool> TryCloseAsync()
    {
        try
        {
            INavigation? owner = Navigation.ModalStack.Contains(this) ? Navigation : Shell.Current?.Navigation;

            // Not on either stack (stale bookkeeping): still attempt the normal path so the popup
            // is not left stranded, and let the exception path report the outcome.
            owner ??= Navigation;

            await owner.PopModalAsync();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StreakSaver] About popup could not be dismissed: {ex}");
            return false;
        }
    }

    // ── Onboarding / Welcome: Continue button ──────────────────────────────
    private async void OnCloseClicked(object sender, EventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        Preferences.Default.Set("LastAppVersionSeen", _assignedVersion);

        // Re-arm the button when the dismissal failed. Leaving _isClosing set would make every
        // later tap a no-op, so the only way out was to kill the app.
        if (!await TryCloseAsync())
            _isClosing = false;
    }

    // ── Update: Later button ───────────────────────────────────────────────
    private async void OnLaterClicked(object sender, EventArgs e)
    {
        if (_isClosing || _isDownloading) return;
        _isClosing = true;

        Preferences.Default.Set("LastRemoteVersionSeen", _assignedVersion);

        if (!await TryCloseAsync())
            _isClosing = false;
    }

    // ── Update: Install button ─────────────────────────────────────────────
    private async void OnInstallClicked(object? sender, EventArgs e)
    {
        if (_isDownloading) return;

#if IOS
        var url = _apkDownloadUrl ?? "https://github.com/PATRICK297R/Tiktok-Streak-Saver/releases/latest";
        await Launcher.OpenAsync(url);
        Preferences.Default.Set("LastRemoteVersionSeen", _assignedVersion);
        await TryCloseAsync();
        return;
#endif

        // If no APK asset found, fall back to opening GitHub releases page
        if (string.IsNullOrEmpty(_apkDownloadUrl))
        {
            await FallbackToGitHubAsync();
            return;
        }

        _isDownloading = true;
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        InstallButton.Text = Localization.T("About_DownloadingShort");

        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<double>(percent =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                int pct = (int)Math.Round(percent * 100);
                ProgressLabel.IsVisible = true;
                ProgressLabel.Text = string.Format(Localization.T("About_DownloadingUpdate"), pct);
            });
        });

        var updateService = new UpdateService();
        string? apkPath = await updateService.DownloadApkAsync(
            _apkDownloadUrl,
            _assignedVersion,
            progress,
            _downloadCts.Token);

        if (apkPath == null || !File.Exists(apkPath))
        {
            ShowDownloadError();
            return;
        }

        // Pre-mark version state BEFORE handing off to installer.
        // When Android restarts the app after install, CheckStartupPopupAsync will
        // detect UpdateJustInstalled=true and skip straight past the Welcome popup.
        Preferences.Default.Set("UpdateJustInstalled", true);
        Preferences.Default.Set("LastRemoteVersionSeen", _assignedVersion);
        Preferences.Default.Set("LastAppVersionSeen", _assignedVersion);

        ProgressLabel.Text = Localization.T("About_StartingInstaller");

        // Android 8+ will not show the install prompt at all unless this app is allowed to install
        // packages. Without the check the intent either throws or the system shows a permission
        // screen with no explanation, which reads as "the update button is broken". Ask first, then
        // carry on with the download that is already on disk.
        if (!await EnsureCanInstallPackagesAsync())
        {
            _isDownloading = false;
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            InstallButton.Text = Localization.T("About_Install");
            ProgressLabel.IsVisible = true;
            ProgressLabel.Text = Localization.T("About_NeedInstallPermission");
            return;
        }

        TriggerApkInstall(apkPath);

        // The installer runs in its own task, so this popup is no longer the foreground window.
        // Closing it here keeps the user from landing back on a stale "Downloading..." popup after
        // the install completes and the app is restarted.
        await TryCloseAsync();
    }

    /// <summary>
    /// True when this app is allowed to install packages, prompting the user once if it is not.
    /// <para>
    /// Always true below Android 8, where the permission does not exist. On 8+ the user has to grant
    /// it from a system screen; there is no API to grant it silently, by design.
    /// </para>
    /// </summary>
    private static async Task<bool> EnsureCanInstallPackagesAsync()
    {
#if ANDROID
        if (!Android.OS.Build.VERSION.SdkInt.AtLeast(Android.OS.BuildVersionCodes.O))
            return true;

        var context = Android.App.Application.Context;
        var packageManager = context.PackageManager;
        if (packageManager is null || context.PackageName is null)
            return true;

        if (packageManager.CanRequestPackageInstalls())
            return true;

        var opened = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            try
            {
                var intent = new Android.Content.Intent(
                    Android.Provider.Settings.ActionManageUnknownAppSources,
                    Android.Net.Uri.Parse($"package:{context.PackageName}"));
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                context.StartActivity(intent);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StreakSaver] Could not open install-permission screen: {ex.Message}");
                return false;
            }
        });

        return opened && packageManager.CanRequestPackageInstalls();
#else
        await Task.CompletedTask;
        return true;
#endif
    }

    private void ShowDownloadError()
    {
        _isDownloading = false;
        InstallButton.IsEnabled = true;
        LaterButton.IsEnabled = true;
        InstallButton.Text = Localization.T("About_Install");
        ProgressLabel.IsVisible = true;
        ProgressLabel.Text = Localization.T("About_DownloadFailed");
        ProgressLabel.TextColor = Color.FromArgb("#D94A4A");

        SwitchToFallbackMode();
    }

    private void SwitchToFallbackMode()
    {
        if (_isFallbackMode) return;
        _isFallbackMode = true;

        InstallButton.Text = Localization.T("About_OpenGithubPage");
        InstallButton.Clicked -= OnInstallClicked;
        InstallButton.Clicked += OnFallbackClicked;
    }

    private async void OnFallbackClicked(object? sender, EventArgs e)
    {
        await FallbackToGitHubAsync();
    }

    private async Task FallbackToGitHubAsync()
    {
        try
        {
            // Points at this fork's releases, not the upstream project it used to name - a user sent
            // there would have downloaded someone else's build.
            await Launcher.Default.OpenAsync("https://github.com/PATRICK297R/Tiktok-Streak-Saver/releases/latest");
        }
        catch { }
    }

    private async void OnFeenerCreditTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            await Launcher.Default.OpenAsync("https://github.com/eulfn/streak-tiktok");
        }
        catch { }
    }

    private void TriggerApkInstall(string apkPath)
    {
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            var file = new Java.IO.File(apkPath);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                context,
                $"{context.PackageName}.fileProvider",
                file);

            // Intent.ActionView with the APK MIME type is the correct cross-version approach.
            // ActionInstallPackage is deprecated on API 29+ and triggers CA1422.
            var intent = new Android.Content.Intent(Android.Content.Intent.ActionView);
            intent.SetDataAndType(uri, "application/vnd.android.package-archive");
            intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StreakSaver] APK install intent failed: {ex.Message}");

            // Show user-friendly explanation for the most common failure cause.
            // INSTALL_FAILED_UPDATE_INCOMPATIBLE means the update APK was signed with
            // a different keystore than the installed version — Android blocks this.
            // The user must uninstall the existing app first, then reinstall from GitHub.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ProgressLabel.IsVisible = true;
                ProgressLabel.Text = Localization.T("About_InstallFailed");
                ProgressLabel.TextColor = Color.FromArgb("#D94A4A");
                _isDownloading = false;
                InstallButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
                SwitchToFallbackMode();
            });
        }
#endif
    }

    /// <summary>Intercept Android hardware back button to behave consistently with Later.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_isDownloading)
        {
            _downloadCts?.Cancel();
        }
        if (_isUpdate)
            OnLaterClicked(this, EventArgs.Empty);
        else
            OnCloseClicked(this, EventArgs.Empty);
        return true;
    }
}
