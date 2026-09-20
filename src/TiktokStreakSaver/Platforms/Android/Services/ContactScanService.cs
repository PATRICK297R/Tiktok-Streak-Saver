using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Webkit;
using AndroidX.Core.App;
using Java.Interop;
using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using WebView = Android.Webkit.WebView;

namespace TiktokStreakSaver.Platforms.Android.Services;

/// <summary>
/// Reads the conversations in the user's TikTok inbox while the app is closed.
/// <para>
/// The scan used to run inside the import page, which tied it to that page: a long friend list meant
/// sitting and watching it, and closing the app abandoned the scan. It now runs here, in a foreground
/// service that owns its own headless WebView, so it continues while the app is closed and reports what
/// it is doing in a notification.
/// </para>
/// <para>
/// The WebView setup is deliberately identical to <see cref="StreakService"/>'s, viewport included. That
/// one is known to work against the real inbox, and the parts of it that look arbitrary - the wide
/// viewport, the 1920x1080 layout, the desktop user agent - are exactly the parts that decide whether
/// TikTok's virtualised chat list renders at all. A headless WebView is 0x0 and renders nothing.
/// </para>
/// </summary>
[Service(Name = AppConstants.PackageName + ".Services.ContactScanService",
         ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class ContactScanService : Service
{
    /// <summary>Quiet, ongoing progress. Low importance so it never makes a sound.</summary>
    private const string ProgressChannelId = "contact_scan_channel";
    private const string ProgressChannelName = "Chat import";

    /// <summary>The result, which is worth a sound because the user is usually not looking at the app.</summary>
    private const string ResultChannelId = "contact_scan_result_channel";
    private const string ResultChannelName = "Chat import results";

    /// <summary>
    /// Kept well clear of the streak service's 1001/1002: those two are its ongoing notification and its
    /// final one, and an import that reused either would silently replace the other's.
    /// </summary>
    private const int NotificationId = 2001;

    /// <summary>
    /// The result is a separate notification id on purpose. <c>StopForeground(Remove)</c> cancels the
    /// notification the service was started with, so posting the result under that same id would have it
    /// wiped the instant the service stops - which is the one notification the user actually needs to see.
    /// </summary>
    private const int ResultNotificationId = 2002;
    private const string ScanScriptName = "tiktok_read_contacts.js";
    private const string MessagesUrl = "https://www.tiktok.com/messages?lang=en";

    /// <summary>How long without a report before the reader is treated as stopped.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long to wait for the first report before concluding the script cannot run on this page.</summary>
    private static readonly TimeSpan ScanDeadline = TimeSpan.FromSeconds(90);

    /// <summary>How often to resend the script while nothing has come back.</summary>
    private static readonly TimeSpan ReInjectEvery = TimeSpan.FromSeconds(6);

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the device is kept awake. A scan opens every conversation in turn, which takes a couple of
    /// seconds each, so this is a generous ceiling for a long friend list rather than an expectation.
    /// </summary>
    private static readonly TimeSpan WakeLockTimeout = TimeSpan.FromMinutes(60);

    /// <summary>Stops a scan that is already in progress from being started a second time.</summary>
    private static readonly object RunLock = new();
    private static volatile bool _isRunning;

    public static bool IsRunning => _isRunning;

    private Handler? _mainHandler;
    private WebView? _webView;
    private PowerManager.WakeLock? _wakeLock;

    private string _script = string.Empty;
    private bool _reportReceived;
    private bool _finished;
    private bool _cancelRequested;

    private DateTime _startedAt;
    private DateTime _lastReportAt;
    private DateTime _lastInjectionAt;
    private int _injectionAttempts;
    private int _stallCount;
    private string _lastScriptLog = string.Empty;

    /// <summary>
    /// The last URL the reader was deliberately not sent to, so that is logged once per page rather than
    /// on every watchdog tick.
    /// </summary>
    private string? _lastSkippedUrl;

    private DateTime _lastNotificationAt;
    private int _lastNotifiedAttempts = -1;

    /// <summary>
    /// Every chat read so far, kept here rather than in the page.
    /// <para>
    /// The page's JavaScript context does not survive a navigation, and opening a chat can navigate, so a
    /// scanner that restarts reports only what it found since. Accumulating here means a restart adds to
    /// what is known instead of replacing it.
    /// </para>
    /// </summary>
    private readonly List<FriendConfig> _accumulated = new();

    private int _attempts;
    private string _url = string.Empty;
    private string _userAgent = string.Empty;
    private List<string> _scriptLog = new();

    private static ContactScanStore Store => ContactScanHub.Store;

    public override void OnCreate()
    {
        base.OnCreate();

        // Before anything else: the notification is what makes this a foreground service, and on Android
        // 8+ a channel that does not exist yet means the notification is silently dropped.
        CreateChannels();

        _mainHandler = new Handler(Looper.MainLooper!);
        AcquireWakeLock();

        // Started in OnCreate, like the streak service, so the five-second window Android allows for
        // reaching the foreground cannot be missed by anything that happens first.
        StartForegroundImmediate();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == AppConstants.ActionStopContactScan)
        {
            _cancelRequested = true;

            lock (RunLock)
            {
                if (!_isRunning)
                {
                    // A stop that arrives after the scan has already ended starts a fresh service instance.
                    // Letting it through would overwrite the finished results with "stopped before anything
                    // could be read" - throwing away the list the user was about to review, because a stale
                    // notification was tapped a moment too late.
                    LogDiagnostic("stop ignored — no import is running");
                    StopForeground(StopForegroundFlags.Remove);
                    StopSelf();
                    return StartCommandResult.NotSticky;
                }
            }

            LogDiagnostic("stop requested by the user");
            Finish(ContactScanPhase.Cancelled, "Stopped.");
            return StartCommandResult.NotSticky;
        }

        StartForegroundImmediate();

        lock (RunLock)
        {
            if (_isRunning)
            {
                // A second request while a scan is in flight is ignored rather than allowed to start a
                // second WebView over the same session.
                LogDiagnostic("start ignored — an import is already running");
                return StartCommandResult.NotSticky;
            }

            _isRunning = true;
        }

        // Reset before anything else is scheduled. StopSelf is asynchronous, so a start that follows a
        // finished scan - "Scan again" pressed straight away - can be delivered to this same instance while
        // it still believes it has finished, and every step of the new scan would then be skipped.
        ResetForNewScan();

        _mainHandler?.Post(StartScan);

        // Not sticky on purpose. A streak run can be resumed after a kill because it is a sequence of
        // sends it can pick up where it left off; a scan cannot, because the half-read page it was working
        // against is gone, and restarting it silently would read the whole inbox again.
        return StartCommandResult.NotSticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    /// <summary>
    /// Puts this instance back to the state a scan starts in.
    /// <para>
    /// Only needed because a service instance can outlive the scan it ran: the instance is torn down
    /// asynchronously, so a new request can arrive first. Everything the scan accumulates has to be cleared
    /// here, or the new scan would start from the previous one's results.
    /// </para>
    /// </summary>
    private void ResetForNewScan()
    {
        _finished = false;
        _cancelRequested = false;
        _reportReceived = false;
        _stallCount = 0;
        _injectionAttempts = 0;
        _lastScriptLog = string.Empty;

        _attempts = 0;
        _url = string.Empty;
        _scriptLog = new List<string>();
        _accumulated.Clear();

        _lastNotifiedAttempts = -1;
        _lastNotificationAt = DateTime.MinValue;
        _lastSkippedUrl = null;

        _startedAt = DateTime.Now;
        _lastReportAt = DateTime.Now;
        _lastInjectionAt = DateTime.MinValue;
    }

    public override void OnDestroy()
    {
        lock (RunLock)
        {
            _isRunning = false;
        }

        ReleaseWakeLock();
        CleanupWebView();
        base.OnDestroy();
    }

    // ─── Keeping the device awake ────────────────────────────────────────────

    /// <summary>
    /// A scan is long and its work happens in a WebView, which stops running JavaScript when the device
    /// sleeps. Without the wake lock the scan would simply stop when the screen goes off - which is the
    /// normal case here, because the user is told they can put the phone down.
    /// </summary>
    private void AcquireWakeLock()
    {
        try
        {
            var powerManager = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = powerManager?.NewWakeLock(WakeLockFlags.Partial, "TiktokStreakSaver::ContactScanWakeLock");
            _wakeLock?.Acquire((long)WakeLockTimeout.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ContactScanService: wake lock failed — {ex.Message}");
        }
    }

    private void ReleaseWakeLock()
    {
        try
        {
            if (_wakeLock?.IsHeld == true)
                _wakeLock.Release();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ContactScanService: wake lock release failed — {ex.Message}");
        }
    }

    // ─── Notifications ───────────────────────────────────────────────────────

    private void CreateChannels()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            return;

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null)
            return;

        if (manager.GetNotificationChannel(ProgressChannelId) is null)
        {
            var progress = new NotificationChannel(ProgressChannelId, ProgressChannelName, NotificationImportance.Low)
            {
                Description = "Shows how many of your chats have been read"
            };
            progress.SetShowBadge(false);
            manager.CreateNotificationChannel(progress);
        }

        if (manager.GetNotificationChannel(ResultChannelId) is null)
        {
            manager.CreateNotificationChannel(new NotificationChannel(
                ResultChannelId, ResultChannelName, NotificationImportance.Default)
            {
                Description = "Tells you when your chats have been read and are ready to review"
            });
        }
    }

    private void StartForegroundImmediate()
    {
        try
        {
            var notification = BuildProgressNotification("Opening your inbox…", withStopAction: false);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(NotificationId, notification, global::Android.Content.PM.ForegroundService.TypeDataSync);
            else
                StartForeground(NotificationId, notification);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ContactScanService: StartForeground failed — {ex.Message}");
        }
    }

    /// <summary>
    /// Shows how far along the scan is.
    /// <para>
    /// The progress bar is indeterminate on purpose: the scanner only learns how many conversations there
    /// are by reaching the end of them, so there is no total to divide by. The counts in the text are what
    /// carry the sense of progress, which is also why this is only redrawn when the count moves.
    /// </para>
    /// </summary>
    private void UpdateProgressNotification(string text, bool force = false)
    {
        var now = DateTime.Now;
        if (!force && now - _lastNotificationAt < TimeSpan.FromSeconds(1))
            return;

        _lastNotificationAt = now;

        try
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.Notify(NotificationId, BuildProgressNotification(text, withStopAction: true));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ContactScanService: progress notification failed — {ex.Message}");
        }
    }

    private Notification BuildProgressNotification(string text, bool withStopAction)
    {
        var builder = new NotificationCompat.Builder(this, ProgressChannelId)
            .SetContentTitle("Reading your TikTok chats")
            .SetContentText(text)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(text))
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentIntent(OpenAppIntent())
            .SetOngoing(true)
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)
            .SetCategory(NotificationCompat.CategoryService)
            .SetPriority(NotificationCompat.PriorityLow)
            .SetProgress(0, 0, true);

        if (withStopAction)
            builder.AddAction(0, "Stop", StopIntent());

        return builder.Build()!;
    }

    /// <summary>
    /// The notification that stays after the scan ends, and the only thing the user sees if it ended while
    /// the app was closed. It is dismissible and makes a sound, because by then there is nothing running
    /// and something waiting to be looked at.
    /// </summary>
    private void ShowResultNotification(ContactScanSnapshot snapshot)
    {
        try
        {
            var text = ContactScanSnapshot.SummaryText(snapshot);

            var notification = new NotificationCompat.Builder(this, ResultChannelId)
                .SetContentTitle("TikTok Streak Saver")
                .SetContentText(text)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(text))
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetContentIntent(OpenAppIntent())
                .SetAutoCancel(true)
                .SetCategory(NotificationCompat.CategoryStatus)
                .SetPriority(NotificationCompat.PriorityDefault)
                .Build();

            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.Notify(ResultNotificationId, notification);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ContactScanService: result notification failed — {ex.Message}");
        }
    }

    /// <summary>
    /// Tapping the notification opens the app, on the import page, where the results are waiting.
    /// <para>
    /// The extra is what makes that happen: without it the app is simply brought to the front on whichever
    /// tab was open, and a finished import is invisible unless the user remembers where to look for it.
    /// Both the progress notification and the result one carry it, so tapping either lands in the same
    /// place - progress while it runs, the list when it is done.
    /// </para>
    /// </summary>
    private PendingIntent OpenAppIntent()
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        intent.PutExtra(AppConstants.ExtraOpenContactImport, true);
        return PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    /// <summary>
    /// The Stop button on the notification, so a scan can be abandoned without opening the app - which is
    /// the whole point of it being able to run with the app closed.
    /// </summary>
    private PendingIntent StopIntent()
    {
        var intent = new Intent(this, typeof(ContactScanService));
        intent.SetAction(AppConstants.ActionStopContactScan);
        return PendingIntent.GetService(this, 1, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    // ─── Starting the scan ───────────────────────────────────────────────────

    private async void StartScan()
    {
        try
        {
            _startedAt = DateTime.Now;
            _lastInjectionAt = DateTime.MinValue;

            var session = new SessionService();
            if (!session.IsSessionValid())
            {
                Finish(ContactScanPhase.Failed, "TikTok asked you to sign in again.");
                return;
            }

            if (!NetworkConnectivity.HasWifiOrCellularInternet(this))
            {
                Finish(ContactScanPhase.Failed, "There is no internet connection.");
                return;
            }

            Publish(ContactScanPhase.Starting, string.Empty);
            LogDiagnostic("import started in the background");

            // Read from the APK's assets rather than through MAUI's file system: this service can be the
            // only thing running, and assets are reachable from any Android context.
            using (var stream = Assets!.Open(ScanScriptName))
            using (var reader = new StreamReader(stream))
            {
                _script = ScriptPreparation.ForInjection(await reader.ReadToEndAsync());
            }

            _userAgent = session.GetLoginUserAgent() ?? AppConstants.DesktopChromeUserAgent;
            BuildWebView(_userAgent);
        }
        catch (Exception ex)
        {
            Finish(ContactScanPhase.Failed, $"The reader could not start: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the headless WebView and opens the inbox in it.
    /// <para>
    /// The configuration is not written out here but taken from
    /// <see cref="TikTokWebViewHelper.ConfigureAndroidScannerWebView"/>, which is where the hard-won part
    /// lives: a desktop user agent is not enough, because the layout TikTok serves follows the viewport
    /// width - a WebView only as wide as the phone is served the mobile layout even with a desktop agent,
    /// and the conversation rows in that layout match nothing the scanner looks for. The result is a page
    /// with the list plainly on screen and a scan that finds nothing. The user agent is the one captured at
    /// login, because the session cookies were issued against it.
    /// </para>
    /// </summary>
    private void BuildWebView(string userAgent)
    {
        _webView = new WebView(this);

        TikTokWebViewHelper.ConfigureAndroidScannerWebView(_webView, userAgent);
        _webView.Settings.SetSupportZoom(true);
        _webView.Settings.BuiltInZoomControls = true;

        // Laid out at a real size for the same reason StreakService does it: a WebView that was never
        // measured is 0x0, and TikTok's virtualised chat list only renders what it believes is on screen.
        _webView.Layout(0, 0, 1920, 1080);

        _webView.SetWebViewClient(new ScanWebViewClient(this));
        _webView.AddJavascriptInterface(new ScanJsInterface(this), ContactScanReader.BridgeName);
        _webView.LoadUrl(MessagesUrl);

        // TikTok occasionally answers the first request with a redirect that is not the inbox. Retrying
        // once covers that; a second failure is reported rather than retried forever.
        _mainHandler?.PostDelayed(() =>
        {
            if (_finished)
                return;

            if (!(_webView?.Url ?? string.Empty).Contains("tiktok.com/messages"))
            {
                _webView?.LoadUrl(MessagesUrl);
                _mainHandler?.PostDelayed(() =>
                {
                    if (!_finished && !(_webView?.Url ?? string.Empty).Contains("tiktok.com/messages"))
                        Finish(ContactScanPhase.Failed, "Could not open tiktok.com/messages.");
                }, 5000);
            }
        }, 5000);

        ScheduleWatchdog();
    }

    private void CleanupWebView()
    {
        _mainHandler?.Post(() =>
        {
            try
            {
                _webView?.StopLoading();
                _webView?.Destroy();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ContactScanService: WebView cleanup failed — {ex.Message}");
            }
            finally
            {
                _webView = null;
            }
        });
    }

    // ─── Watchdog ────────────────────────────────────────────────────────────

    private void ScheduleWatchdog()
    {
        if (_finished)
            return;

        _mainHandler?.PostDelayed(WatchdogTick, (long)WatchdogInterval.TotalMilliseconds);
    }

    private void WatchdogTick()
    {
        if (_finished)
            return;

        try
        {
            Watchdog();
        }
        catch (Exception ex)
        {
            LogDiagnostic($"watchdog error: {ex.Message}");
        }

        ScheduleWatchdog();
    }

    /// <summary>
    /// Keeps the scan alive, and ends it when it is clear it will not finish.
    /// <para>
    /// The same shape as the version that used to run in the page, which was written after a single
    /// injection attempt that landed at the wrong moment left the page waiting with nothing to show for
    /// it. Retrying costs nothing, because the script refuses to restart itself.
    /// </para>
    /// </summary>
    private void Watchdog()
    {
        if (_cancelRequested)
            return;

        if (_reportReceived)
        {
            if (DateTime.Now - _lastReportAt <= StallTimeout)
                return;

            // Nothing new for a while. The page's JavaScript context does not survive a navigation, and
            // opening a chat can navigate, so the reader may simply no longer exist.
            _stallCount++;
            LogDiagnostic($"scan stalled (report {_stallCount}), {_accumulated.Count} chats known so far");

            if (_stallCount >= 2)
            {
                // Resending did not bring it back. What was read is still worth offering, and the message
                // travels with it so the page can say the list is partial.
                Finish(ContactScanPhase.Finished, "The reader stopped responding before the end of the list.");
                return;
            }

            _lastReportAt = DateTime.Now;
            _lastInjectionAt = DateTime.MinValue;
            Inject();
            UpdateProgressNotification($"Reading your chats… restarting the reader (attempt {_injectionAttempts})", force: true);
            return;
        }

        if (DateTime.Now - _lastInjectionAt > ReInjectEvery)
        {
            Inject();

            // The attempt count is only worth showing once something has actually been sent. Held back
            // because the inbox has not loaded, it would read "attempt 0" and look broken.
            UpdateProgressNotification(
                _injectionAttempts == 0
                    ? "Opening your inbox…"
                    : $"Opening your inbox… (attempt {_injectionAttempts})",
                force: true);
            return;
        }

        if (DateTime.Now - _startedAt > ScanDeadline)
        {
            // Which of the two failures this was matters, because they are fixed in different places: a page
            // that never loaded is the connection, a script that never ran is the app.
            Finish(ContactScanPhase.Failed, _lastSkippedUrl is not null
                ? "TikTok's inbox never loaded — the page stayed blank. Check your connection, then try again."
                : "The reader did not start on the page.");
        }
    }

    /// <summary>
    /// Sends the scanner into the page, but only when the page is TikTok's inbox.
    /// <para>
    /// The gate is the important part. A WebView starts on a blank document and can end up on one again
    /// after a load it cannot perform, and the reader cannot tell that from an inbox with no conversations
    /// in it: it waits for conversation rows, finds none, and reports a finished scan with nothing read.
    /// Letting it run there produces a scan that looks like a successful read of an empty inbox and reports
    /// <c>about:blank</c> as the page it read - a dead end for the user and no use as a diagnosis. So it
    /// waits for the real page instead, and the log says which page it was waiting on.
    /// </para>
    /// </summary>
    private void Inject()
    {
        if (_finished || _script.Length == 0 || _webView is null)
            return;

        var url = _webView.Url;

        if (!WebViewNavigationPolicy.IsInbox(url))
        {
            if (url != _lastSkippedUrl)
            {
                _lastSkippedUrl = url;
                LogDiagnostic($"reader held back — not the inbox yet ({Describe(url)})");
            }

            return;
        }

        try
        {
            _webView.EvaluateJavascript(_script, null);
            _lastInjectionAt = DateTime.Now;
            _injectionAttempts++;
            LogDiagnostic($"reader script sent (attempt {_injectionAttempts})");
        }
        catch (Exception ex)
        {
            LogDiagnostic($"reader script could not be sent: {ex.Message}");
        }
    }

    /// <summary>Names a URL for the log, including the cases where it has nothing useful in it.</summary>
    private static string Describe(string? url) =>
        WebViewNavigationPolicy.IsBlank(url) ? "a blank page" : url!;

    /// <summary>Opens a URL in the headless WebView, on the main thread.</summary>
    private void LoadUrl(string url)
    {
        _mainHandler?.Post(() =>
        {
            if (_finished)
                return;

            try
            {
                _webView?.LoadUrl(url);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"could not open {url}: {ex.Message}");
            }
        });
    }

    // ─── Receiving reports ───────────────────────────────────────────────────

    /// <summary>
    /// Handles one report from the page.
    /// <para>
    /// The scanner pushes on every line it logs, so the first report already proves the script is running -
    /// which is what makes it possible to tell "the script never ran" from "the script ran and read
    /// nothing".
    /// </para>
    /// </summary>
    internal void OnReported(string json)
    {
        if (_finished)
            return;

        var normalized = ContactScanReader.NormalizeJavaScriptResult(json);
        if (!ContactScanReader.TryParse(normalized, out var scan, out _) || scan is null)
            return;

        _reportReceived = true;
        _lastReportAt = DateTime.Now;
        _stallCount = 0;

        Accumulate(scan);

        _attempts = Math.Max(_attempts, scan.Attempts);
        _url = scan.Url;
        _scriptLog = scan.Log;

        if (!string.IsNullOrWhiteSpace(scan.UserAgent))
            _userAgent = scan.UserAgent;

        // The scanner's own log is what carries the diagnostics - the page shape, the viewport, anything
        // thrown inside the page - and the run log is the only place they survive the app being closed.
        var line = scan.Log.Count > 0 ? scan.Log[^1] : string.Empty;
        if (line.Length > 0 && line != _lastScriptLog)
        {
            _lastScriptLog = line;
            LogDiagnostic($"reader: {line}");
        }

        if (scan.Done)
        {
            // A finished scan that read nothing from a page that is not the inbox is not a result: the reader
            // ran somewhere useless and reported the only thing it could, which is nothing. Offering that to
            // the user is how an import ends at a dead end, so the inbox is reopened and the scan carries on.
            // The overall deadline still bounds how long that can go on.
            if (_accumulated.Count == 0
                && !WebViewNavigationPolicy.IsInbox(scan.Url)
                && DateTime.Now - _startedAt <= ScanDeadline)
            {
                LogDiagnostic($"reader finished on the wrong page ({Describe(scan.Url)}) — reopening the inbox");
                ReopenInbox();
                return;
            }

            LogDiagnostic($"import finished: {_accumulated.Count} chats from {_attempts} opened");
            Finish(ContactScanPhase.Finished, string.Empty);
            return;
        }

        Publish(ContactScanPhase.Running, string.Empty);
    }

    /// <summary>
    /// Reopens the inbox after the reader found itself on a page with nothing in it.
    /// <para>
    /// The report state is reset as well as the page being reloaded: the report that prompted this came from
    /// a document that no longer exists, so it must not count as proof that the reader is running - otherwise
    /// the watchdog would see a live scan and wait for reports that can never come.
    /// </para>
    /// </summary>
    private void ReopenInbox()
    {
        _reportReceived = false;
        _lastInjectionAt = DateTime.MinValue;
        _lastSkippedUrl = null;
        _lastReportAt = DateTime.Now;

        UpdateProgressNotification("Opening your inbox…", force: true);
        LoadUrl(MessagesUrl);
    }

    /// <summary>
    /// Adds a report's chats to what is already known.
    /// <para>
    /// Merged rather than replaced, so a scanner that restarted after a navigation adds to the list instead
    /// of throwing away everything it had already read.
    /// </para>
    /// </summary>
    private void Accumulate(ContactScanDto scan)
    {
        var batch = ContactScanReader.ToFriends(scan.Items);
        if (batch.Count == 0)
            return;

        var merged = BackupMergePolicy.Merge(_accumulated, batch);
        _accumulated.Clear();
        _accumulated.AddRange(merged.Friends);
    }

    // ─── Publishing progress ─────────────────────────────────────────────────

    /// <summary>
    /// Writes the current state where the page can see it, and keeps the notification in step.
    /// <para>
    /// The notification is only redrawn when the number of chats opened has moved: the scanner reports far
    /// more often than that, and a notification is not free to rebuild.
    /// </para>
    /// </summary>
    private void Publish(ContactScanPhase phase, string message)
    {
        var snapshot = new ContactScanSnapshot
        {
            Phase = phase,
            Message = message,
            Attempts = _attempts,
            Items = ContactScanSnapshot.ItemsFrom(_accumulated),
            Log = _scriptLog,
            Url = _url,
            UserAgent = _userAgent,
            StartedAt = _startedAt,
            UpdatedAt = DateTime.UtcNow
        };

        Store.Publish(snapshot);

        if (phase != ContactScanPhase.Running || _attempts == _lastNotifiedAttempts)
            return;

        _lastNotifiedAttempts = _attempts;
        UpdateProgressNotification(ContactScanSnapshot.ProgressText(snapshot));
    }

    /// <summary>
    /// Ends the scan, whatever the reason, and leaves the app in a state that does not depend on this
    /// service still existing.
    /// </summary>
    private void Finish(ContactScanPhase phase, string message)
    {
        if (_finished)
            return;

        _finished = true;

        try
        {
            // First, so the page can never be left waiting on a scan that is already over.
            Publish(phase, message);

            if (Store.Current is { } snapshot)
                ShowResultNotification(snapshot);

            LogDiagnostic($"import stopped: {phase} ({_accumulated.Count} chats, {_attempts} opened)"
                + (message.Length > 0 ? $" — {message}" : string.Empty));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ContactScanService: finishing failed — {ex.Message}");
        }
        finally
        {
            CleanupWebView();
            ReleaseWakeLock();

            lock (RunLock)
            {
                _isRunning = false;
            }

            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    /// <summary>
    /// Records a line in the app's run log, which the History page can export.
    /// <para>
    /// The importer's whole diagnostic trail goes through here. It is written to the service's log rather
    /// than to a file of its own so an export shows the import and any streak run together, in the order
    /// they happened.
    /// </para>
    /// </summary>
    private static void LogDiagnostic(string message)
    {
        try
        {
            StreakService.AppendExternalLog("IMPORT", "-", message);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ContactScanService: logging failed — {ex.Message}");
        }
    }

    // ─── Bridge and page events ──────────────────────────────────────────────

    /// <summary>
    /// The bridge the scanner reports through. Reports arrive on the WebView's JavaScript thread, so they
    /// are handed to the main thread - the same arrangement the streak service uses.
    /// </summary>
    [Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
    internal sealed class ScanJsInterface : Java.Lang.Object
    {
        private readonly ContactScanService _service;

        public ScanJsInterface(ContactScanService service)
        {
            _service = service;
        }

        [JavascriptInterface]
        [Export("report")]
        public void Report(string json)
        {
            _service._mainHandler?.Post(() => _service.OnReported(json));
        }
    }

    [Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
    private sealed class ScanWebViewClient : WebViewClient
    {
        private readonly ContactScanService _service;

        public ScanWebViewClient(ContactScanService service)
        {
            _service = service;
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);

            if (string.IsNullOrEmpty(url))
                return;

            // A session can be rejected even when the app still believes it is signed in. Without this the
            // scan would sit waiting for a chat list that is never coming.
            if (WebViewNavigationPolicy.IsLoginWall(url))
            {
                LogDiagnostic($"login wall at {url}");
                _service.Finish(ContactScanPhase.Failed, "TikTok asked you to sign in again.");
                return;
            }

            // Injected on every navigation until results arrive: a full navigation wipes the page's
            // JavaScript context, taking a scan that was already running with it.
            _service.Inject();
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url?.ToString();
            if (string.IsNullOrEmpty(url))
                return false;

            if (WebViewNavigationPolicy.IsLoadable(url))
                return false;

            // TikTok signals its native app to open with custom schemes (snssdk1180://, intent://). A WebView
            // cannot load those, and Android replaces them with a blank error page - which is one of the ways
            // the reader ends up running on a document with nothing in it. Cancelled instead, and the real
            // destination followed when the URL carries one. These are the same rules the login WebView uses,
            // which is why they live in the shared policy rather than being written out again here.
            var target = WebViewNavigationPolicy.ExtractHttpDestination(url);
            if (target is not null)
                _service.LoadUrl(target);

            return true;
        }
    }
}
