using Android.App;
using Android.Content;
using Android.OS;
using Android.Webkit;
using Android.Runtime;
using AndroidX.Core.App;
using Android.Content.PM;
using Java.Interop;
using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using TiktokStreakSaver.Platforms.Android;
using WebView = Android.Webkit.WebView;

namespace TiktokStreakSaver.Platforms.Android.Services;

[Service(Name = AppConstants.PackageName + ".Services.StreakService", ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class StreakService : Service
{
    private const string ChannelId = "streak_service_channel";
    private static string ChannelName => Localization.T("Android_StreakChannelName");
    /// <summary>Ongoing foreground + completion / offline alerts that should be visible (not Low importance).</summary>
    private const string StatusChannelId = "streak_status_channel";
    private static string StatusChannelName => Localization.T("Android_StatusChannelName");
    private const int NotificationId = 1001;

    private WebView? _webView;
    private Handler? _mainHandler;
    private SettingsService? _settingsService;
    private List<FriendConfig>? _friendsToProcess;
    private int _currentFriendIndex;
    private StreakRunResult? _runResult;

    /// <summary>
    /// Times the run so the Activity page can say how long it took. Nothing recorded this before, so
    /// the page had no duration to show and reported a run it had already saved as "Last run: N/A".
    /// </summary>
    private System.Diagnostics.Stopwatch? _runStopwatch;
    private PowerManager.WakeLock? _wakeLock;
    private string _baseScript = string.Empty;
    private readonly List<string> _disabledUsernames = new();
    private const string UserNotFoundError = "User not found in chat list";

    /// <summary>
    /// Intent extra naming the chats a retry should process. Absent for a normal scheduled run.
    /// </summary>
    public const string ExtraTargetFriendIds = "target_friend_ids";

    /// <summary>
    /// Intent extra marking a run the user started by tapping Run Now.
    /// <para>
    /// A scheduled run honours the "already messaged today" cooldown; a manual run does not, because
    /// the user is pressing the button precisely to send again. Carried per-intent so a later
    /// scheduled run cannot inherit the exemption.
    /// </para>
    /// </summary>
    public const string ExtraManualRun = "manual_run";

    /// <summary>
    /// True when this run was started by the user from the Dashboard rather than by the schedule.
    /// </summary>
    private bool _isManualRun;

    /// <summary>
    /// When set, this run processes only these friend ids and ignores the "already sent today" skip.
    /// <para>
    /// A normal run skips anyone messaged today, which is what stops a double send. A retry exists
    /// precisely because those chats failed, so the skip has to be bypassed for them - and only them,
    /// which is why the ids are carried rather than a simple "ignore cooldown" flag.
    /// </para>
    /// </summary>
    private HashSet<string>? _targetFriendIds;

    // ── Randomized Normal Messages state ──
    private List<string>? _shuffledNormalMessages;
    private int _normalMessageIndex = 0;

    private readonly Random _rng = new();

    // ── Service lifecycle flags ──
    private bool _isCancelRequested = false;
    private bool _automationStarted = false;

    // ── Run-level mutex: prevents concurrent automation sessions ──
    private static volatile bool _isRunning = false;
    private static readonly object _runLock = new();

    /// <summary>
    /// True while an automation session is active. Checked by StreakScheduler.RunNow
    /// and OnStartCommand to prevent overlapping runs.
    /// </summary>
    public static bool IsRunning => _isRunning;

    /// <summary>
    /// The run currently in flight, or null when nothing is running.
    /// <para>
    /// Published so the Dashboard and History can show progress as it happens. Both used to read
    /// <see cref="SettingsService.GetRunHistory"/>, which only gains the run once CompleteService
    /// has written it - so a five minute run showed an empty bar until the moment it finished, and
    /// the per-chat results were invisible while they were being produced.
    /// </para>
    /// <para>
    /// The list is mutated by the service thread while the UI reads it, so callers must snapshot
    /// under <see cref="LiveRunLock"/> rather than enumerate it directly.
    /// </para>
    /// </summary>
    public static StreakRunResult? LiveRun { get; private set; }

    /// <summary>Guards <see cref="LiveRun"/> and its FriendResults while a run is in flight.</summary>
    public static readonly object LiveRunLock = new();

    /// <summary>
    /// Snapshot of the in-flight run's per-chat results, safe to enumerate from the UI thread.
    /// Returns an empty list when no run is active.
    /// </summary>
    public static List<FriendMessageResult> LiveRunResults()
    {
        lock (LiveRunLock)
        {
            return LiveRun is null
                ? new List<FriendMessageResult>()
                : LiveRun.FriendResults.ToList();
        }
    }

    private int _cooldownSkippedCount = 0;

    private int _failureAttemptsForCurrentFriend;
    private const int MaxSendAttemptsPerFriend = 4;
    private bool _allowSendRetries = true;
    private Action? _currentFriendTimeoutAction;
    private const long FriendTimeoutMs = 75000; // 75 seconds watchdog per friend

    /// <summary>
    /// Verification reported by the script for the send currently in flight: true when the composer
    /// cleared, false when it did not. Null means nothing was reported (a failed send, or an older
    /// script build that has no verification).
    /// </summary>
    private bool? _pendingVerification;

    /// <summary>
    /// The text of the send currently in flight, kept so the result can record exactly what went out.
    /// </summary>
    private string? _currentSendText;

    /// <summary>
    /// When the send currently in flight was started. The delivery rescan opens its time window here.
    /// </summary>
    private DateTime _currentSendStartedAt;

    /// <summary>True while the second pass is running, so a stray send cannot be started from it.</summary>
    private bool _isRescanning;

    /// <summary>
    /// Chats to re-check in the delivery rescan, and how far through them the pass has got.
    /// </summary>
    private List<FriendConfig>? _rescanQueue;
    private int _rescanIndex;

    /// <summary>Tally of the rescan, for the log line and the closing notification.</summary>
    private int _rescanFound;
    private int _rescanAbsent;
    private int _rescanUnknown;

    /// <summary>
    /// The chats the run was asked to process, kept aside while the resend borrows
    /// <see cref="_friendsToProcess"/> for its own queue.
    /// </summary>
    private List<FriendConfig>? _runFriends;

    /// <summary>
    /// Rescan passes already run. One is enough: a second pass would only re-examine the same chats, and
    /// the resend it triggers is what actually fixes them.
    /// </summary>
    private const int MaxRescanPasses = 1;
    private int _rescanPass;

    /// <summary>
    /// True while the rescan's resend is in flight, so the result updates the existing row instead of
    /// adding a second one for the same chat.
    /// </summary>
    private bool _isResending;

    /// <summary>
    /// Chats already resent once. A resend that fails again is recorded as failed and left alone - the
    /// point of the pass is to repair a missed message, not to loop until the streak is satisfied.
    /// </summary>
    private readonly HashSet<string> _resentFriendIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Rehearsal mode: walk the automation and confirm each chat is reachable, but never send. The
    /// results are recorded and then excluded from every "sent today" figure.
    /// </summary>
    private bool _dryRun;

    private static List<string> _logs = new();
    private static RunLogStore? _logStore;
    private static readonly object _logLock = new();

    /// <summary>
    /// File-backed log that outlives the process. Without it a run killed mid-flight - the case the
    /// service already anticipates - left no evidence at all.
    /// </summary>
    private static RunLogStore LogStore
    {
        get
        {
            if (_logStore == null)
            {
                var path = System.IO.Path.Combine(FileSystem.AppDataDirectory, AppConstants.RunLogsFileName);
                _logStore = new RunLogStore(path);
            }

            return _logStore;
        }
    }

    private static PendingSendStore? _pendingSendStore;

    /// <summary>
    /// Durable record of the sends attempted today, written before each message goes out and flushed to
    /// disk. This is what stops a run that the system killed from messaging the same friend twice: the
    /// in-memory and Preferences records of a send both depend on the send having finished and the
    /// process having survived it.
    /// </summary>
    private static PendingSendStore PendingSends
    {
        get
        {
            if (_pendingSendStore == null)
            {
                var path = System.IO.Path.Combine(FileSystem.AppDataDirectory, AppConstants.PendingSendsFileName);
                _pendingSendStore = new PendingSendStore(path);
            }

            return _pendingSendStore;
        }
    }

    /// <summary>
    /// Appends a line to the same run log the service writes to, so entries recorded by other screens
    /// appear in the log the Activity page exports.
    /// <para>
    /// Writing straight to the log file is not enough: <see cref="GetLogs"/> returns the in-memory
    /// list whenever it has anything in it, and only falls back to the file when it is empty. A line
    /// written to the file alone would therefore be missing from the export after any run in the same
    /// session - which is precisely when someone is most likely to be looking.
    /// </para>
    /// </summary>
    public static void AppendExternalLog(string phase, string username, string message)
    {
        AppendLogEntry(RunLogStore.Format(phase, username, message, DateTime.Now));
    }

    public static List<string> GetLogs()
    {
        // The in-memory list is authoritative while this process is alive and holds the newest run.
        lock (_logLock)
        {
            if (_logs.Count > 0)
                return new List<string>(_logs);
        }

        // Empty means a fresh process (for example the user opened the app after the service was
        // killed), so fall back to what the previous run managed to persist.
        return LogStore.ReadRecent(RunLogStore.DefaultMaxLines);
    }

    public static void ClearLogs()
    {
        lock (_logLock)
        {
            _logs = new List<string>();
        }

        LogStore.Clear();
    }

    /// <summary>Starts a clean log for a new run, on disk as well as in memory.</summary>
    private static void ResetRunLog()
    {
        lock (_logLock)
        {
            _logs.Clear();
        }

        LogStore.Clear();
    }

    private static void AppendLogEntry(string entry)
    {
        lock (_logLock)
        {
            _logs.Add(entry);
        }

        LogStore.Append(entry);
    }

    private static void AppLog(string phase, string username, string message)
    {
        var entry = RunLogStore.Format(phase, username, message, DateTime.Now);
        AppendLogEntry(entry);
        System.Diagnostics.Debug.WriteLine(entry);
    }

    public override void OnCreate()
    {
        base.OnCreate();

        // Create notification channel FIRST before anything else
        CreateNotificationChannel();
        CreateStatusNotificationChannel();

        _mainHandler = new Handler(Looper.MainLooper!);
        _settingsService = new SettingsService();
        AcquireWakeLock();

        // Start foreground IMMEDIATELY in OnCreate to avoid ANR
        StartForegroundServiceImmediate();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "STOP_SERVICE")
        {
            _isCancelRequested = true;
            AppLog("SYSTEM", "-", "Service stop requested by user");
            CompleteService(false, Localization.T("Android_RunStoppedByUser"));
            return StartCommandResult.NotSticky;
        }

        // Ensure we're in foreground mode (in case OnCreate didn't complete it)
        StartForegroundServiceImmediate();

        // A retry names the chats it wants. Captured before the mutex so a rejected duplicate start
        // cannot leave the ids behind for the next run.
        var targets = intent?.GetStringArrayListExtra(ExtraTargetFriendIds);
        var manualRun = intent?.GetBooleanExtra(ExtraManualRun, false) ?? false;

        // ── Run-level mutex: ignore duplicate starts while a session is active ──
        // IMPORTANT: Do not StopSelf() here — a second start (e.g. alarm + Run now) would kill the in-flight run.
        lock (_runLock)
        {
            if (_isRunning)
            {
                AppLog("SYSTEM", "-", "OnStartCommand ignored — automation already running");
                return StartCommandResult.NotSticky;
            }
            _isRunning = true;
        }

        _targetFriendIds = targets is { Count: > 0 } ? new HashSet<string>(targets) : null;
        _isManualRun = manualRun;

        // Start the WebView automation on main thread
        _mainHandler?.Post(StartWebViewAutomation);

        // Sticky: if Android kills the service mid-run, the system will recreate
        // the service so the run can resume.
        return StartCommandResult.Sticky;
    }

    private void StartForegroundServiceImmediate()
    {
        try
        {
            var notification = CreateNotification(Localization.T("Android_PreparingToSend"));

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                // Android 10+ requires specifying the foreground service type
                StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartForeground error: {ex.Message}");
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        // Safety net: release the run-level mutex if the service is destroyed
        // without CompleteService being called (e.g. system kill while WebView is loading).
        lock (_runLock)
        {
            _isRunning = false;
        }
        ReleaseWakeLock();
        CleanupWebView();
        base.OnDestroy();
    }

    private void AcquireWakeLock()
    {
        var powerManager = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = powerManager?.NewWakeLock(WakeLockFlags.Partial, "TiktokStreakSaver::StreakWakeLock");
        // 30 minute ceiling — generous upper bound for a normal run with a large friend list.
        _wakeLock?.Acquire(30L * 60 * 1000);
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            if (notificationManager == null) return;

            // Check if channel already exists
            var existingChannel = notificationManager.GetNotificationChannel(ChannelId);
            if (existingChannel != null) return;

            var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
            {
                Description = Localization.T("Android_StreakChannelDesc")
            };
            channel.SetShowBadge(false);

            notificationManager?.CreateNotificationChannel(channel);
        }
    }

    private void CreateStatusNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            if (notificationManager == null) return;

            if (notificationManager.GetNotificationChannel(StatusChannelId) != null) return;

            var channel = new NotificationChannel(StatusChannelId, StatusChannelName, NotificationImportance.Default)
            {
                Description = Localization.T("Android_StatusChannelDesc")
            };
            notificationManager.CreateNotificationChannel(channel);
        }
    }

    private Notification CreateNotification(string message)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle(Localization.T("Android_NotificationTitle"))
            .SetContentText(message)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(message))
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)
            .SetCategory(NotificationCompat.CategoryService)
            .SetPriority(NotificationCompat.PriorityLow)
            .SetProgress(0, 0, true);

        return builder.Build()!;
    }

    private void UpdateNotification(string message, int progress = -1, int max = 0)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle(Localization.T("Android_NotificationTitle"))
            .SetContentText(message)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(message))
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)
            .SetCategory(NotificationCompat.CategoryService)
            .SetPriority(NotificationCompat.PriorityLow);

        if (progress >= 0 && max > 0)
            builder!.SetProgress(max, progress, false);
        else
            builder!.SetProgress(0, 0, true);

        var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
        notificationManager?.Notify(NotificationId, builder.Build()!);
    }

    private async void StartWebViewAutomation()
    {
        try
        {
            _automationStarted = false;

            _currentFriendIndex = 0;
            _runResult = new StreakRunResult();
            _runStopwatch = System.Diagnostics.Stopwatch.StartNew();
            _cooldownSkippedCount = 0;

            // Published before the first send so the Dashboard can show "0 of N" the moment the run
            // starts, instead of an empty bar that only fills once the run is already over.
            lock (LiveRunLock)
            {
                LiveRun = _runResult;
            }

            // Rehearsal mode is read once per run so toggling it mid-run cannot produce a mixture of
            // real and simulated sends in the same history entry.
            _dryRun = _settingsService?.GetDryRun() ?? false;
            _runResult.DryRun = _dryRun;
            _pendingVerification = null;

            // A new run starts with a clean rescan state. Without this, a second run in the same service
            // instance would inherit the first run's "already rescanned" flag and skip verification.
            _rescanPass = 0;
            _rescanQueue = null;
            _runFriends = null;
            _rescanIndex = 0;
            _isRescanning = false;
            _isResending = false;
            _rescanFound = 0;
            _rescanAbsent = 0;
            _rescanUnknown = 0;
            _resentFriendIds.Clear();

            // Fresh log per run, in memory and on disk.
            ResetRunLog();

            if (_dryRun)
                AppLog("SYSTEM", "-", "Dry run enabled — no messages will be sent");

            _friendsToProcess = new List<FriendConfig>();

            var allEnabled = _settingsService?.GetEnabledFriends() ?? new List<FriendConfig>();
            var today = DateTime.Now.Date;

            // What the durable record says about today. Read before anything is sent, because a run that
            // the system killed mid-send leaves an entry here with no result - and that entry is the only
            // evidence that a message may already have gone out.
            var durableStates = PendingSends.ReadToday(today);

            foreach (var friend in allEnabled)
            {
                // A retry names its chats. Everything else is out of scope for this run, including
                // chats that would otherwise be processed - a retry must not turn into a full run.
                var isTargeted = _targetFriendIds is not null && _targetFriendIds.Contains(friend.Id);
                if (_targetFriendIds is not null && !isTargeted)
                    continue;

                // A manual Run Now ignores both cooldowns below. The user pressed the button to send
                // again, so "already messaged today" is the expected state, not a reason to skip -
                // honouring it turned the tap into a no-op that only reported "already sent". The
                // scheduled path keeps both checks untouched, which is what prevents the 23-hour
                // schedule from double-sending.
                if (_isManualRun)
                {
                    _friendsToProcess.Add(friend);
                    continue;
                }

                if (!isTargeted && friend.LastMessageSent.HasValue && friend.LastMessageSent.Value.Date == today)
                {
                    _cooldownSkippedCount++;
                    AppLog("SKIP", $"@{friend.Username}",
                        $"Already messaged today at {friend.LastMessageSent.Value:HH:mm}");
                    continue;
                }

                // Sent: the message is known to have gone out. Unknown: the send was in flight when the
                // process was killed, so it may have gone out. Either way, sending again would put a second
                // message on the same friend - the pile-up this record exists to prevent - so the entry is
                // left alone for the rest of the day. A definite failure is not skipped: nothing was sent,
                // so a retry is safe.
                //
                // A targeted retry is the exception: the user explicitly asked for these chats, so the
                // durable "sent" entry is cleared and the send is allowed. The cooldown above still
                // protects everything the user did not name.
                if (!isTargeted && durableStates.TryGetValue(friend.Id, out var outcome) && outcome != PendingSendState.Failed)
                {
                    _cooldownSkippedCount++;
                    AppLog("SKIP", $"@{friend.Username}", outcome == PendingSendState.Sent
                        ? "Already messaged today (durable record)"
                        : "May already have been messaged — the previous run was interrupted");
                    continue;
                }

                _friendsToProcess.Add(friend);
            }

            if (_targetFriendIds is not null)
            {
                AppLog("SYSTEM", "-",
                    $"Targeted retry: {_friendsToProcess.Count} of {_targetFriendIds.Count} requested chats are enabled");
            }

            // Yesterday's records cannot affect today, so they are dropped rather than kept forever.
            PendingSends.PruneBefore(today);

            // Initialize randomized message pool if user opted in.
            if (_settingsService?.GetRandomizeNormalMessages() == true)
            {
                _shuffledNormalMessages = new List<string>(SettingsService.BuiltInStreakMessages);
                ShuffleList(_shuffledNormalMessages);
                _normalMessageIndex = 0;
                AppLog("SYSTEM", "-", $"Randomized messages enabled: {_shuffledNormalMessages.Count} variants loaded");
            }
            else
            {
                _shuffledNormalMessages = null;
            }

            AppLog("SYSTEM", "-",
                $"Starting automation: {_friendsToProcess.Count} to process, {_cooldownSkippedCount} skipped (already sent today)");

            if (_friendsToProcess.Count == 0)
            {
                var msg = _cooldownSkippedCount > 0
                    ? string.Format(Localization.T("Android_AllFriendsMessaged"), _cooldownSkippedCount)
                    : Localization.T("Android_NoFriendsConfigured");
                CompleteService(_cooldownSkippedCount > 0, msg);
                return;
            }

            if (!NetworkConnectivity.HasWifiOrCellularInternet(this))
            {
                CompleteSkippedNoNetwork();
                return;
            }

            UpdateNotification(Localization.T("Android_PreparingAutomation"));

            using var resourceStream = await FileSystem.OpenAppPackageFileAsync("tiktok_automation.js");
            using var reader = new StreamReader(resourceStream);
            this._baseScript = await reader.ReadToEndAsync();

            // Flattened before injection: the Android WebView does not reliably evaluate a
            // multi-line script. Shared with the contact scanner so both scripts are prepared the
            // same way.
            this._baseScript = ScriptPreparation.ForInjection(this._baseScript);

            _webView = new WebView(this);
            _webView.Settings.JavaScriptEnabled = true;
            _webView.Settings.DomStorageEnabled = true;
            _webView.Settings.DatabaseEnabled = true;
            _webView.Settings.CacheMode = CacheModes.Normal;

            // Chromium suspends this WebView's JavaScript timers unless they are explicitly resumed.
            // The automation script polls the chat list with setTimeout, so without this every poll
            // after the first one is deferred by up to a minute, the run looks like "no chat items
            // found", and every friend is reported as failed. ResumeTimers() is global, which is why
            // TikTokWebViewHelper deliberately never calls PauseTimers() on the login WebView.
            _webView.ResumeTimers();

            // Reuse the UA captured at login time so cookies stay valid; fall back to a modern Chrome desktop UA.
            var sessionService = new SessionService();
            var loginUa = sessionService.GetLoginUserAgent()
                ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
            _webView.Settings.UserAgentString = loginUa;

            // Layout() takes PHYSICAL pixels, while TikTok picks its layout from CSS pixels. Asking
            // for 1920x1080 directly gave a 640x360 CSS viewport on a 3x-density phone - TikTok's
            // narrow layout, whose conversation rows these selectors do not match. Scaling by density
            // keeps the CSS viewport at 1920x1080 on every device.
            //
            // The three settings below are the combination the upstream project settled on, and they
            // matter together: with UseWideViewPort/LoadWithOverviewMode left ON, SetInitialScale is
            // applied on top of the wide-viewport zoom and the page ends up laid out at a size TikTok
            // does not expect - it serves a skeleton whose chat list never renders. Off, the explicit
            // Layout() below is the only thing deciding the viewport.
            _webView.Settings.SetSupportZoom(false);
            _webView.Settings.BuiltInZoomControls = false;
            _webView.Settings.UseWideViewPort = false;
            _webView.Settings.LoadWithOverviewMode = false;
            // SetInitialScale lives on the WebView, not on WebSettings.
            _webView.SetInitialScale(100);

            var displayMetrics = Resources?.DisplayMetrics;
            var density = displayMetrics?.Density ?? 2.0f;
            _webView.Layout(0, 0, (int)(1920 * density), (int)(1080 * density));

            var cookieManager = CookieManager.Instance;
            cookieManager?.SetAcceptCookie(true);
            cookieManager?.SetAcceptThirdPartyCookies(_webView, true);

            _webView.SetWebViewClient(new StreakWebViewClient(this));
            _webView.AddJavascriptInterface(new StreakJsInterface(this), "StreakApp");
            _webView.LoadUrl("https://www.tiktok.com/messages?lang=en");

            _mainHandler!.PostDelayed(() =>
            {
                if (!WebViewNavigationPolicy.IsInbox(_webView?.Url))
                {
                    _webView?.LoadUrl("https://www.tiktok.com/messages?lang=en");
                    _mainHandler.PostDelayed(() =>
                    {
                        if (!WebViewNavigationPolicy.IsInbox(_webView?.Url))
                        {
                            CompleteService(false, Localization.T("Android_CouldNotNavigate"));
                        }
                    }, 5000);
                }
            }, 5000);
        }
        catch (Exception ex)
        {
            CompleteService(false, string.Format(Localization.T("Android_ErrorStartingWebView"), ex.Message));
        }
    }

    private void CleanupWebView()
    {
        if (_currentFriendTimeoutAction != null)
        {
            _mainHandler?.RemoveCallbacks(_currentFriendTimeoutAction);
            _currentFriendTimeoutAction = null;
        }

        _mainHandler?.Post(() =>
        {
            _webView?.StopLoading();
            _webView?.Destroy();
            _webView = null;
        });
    }

    internal void OnPageLoaded(string url)
    {
        // Check if we're on the messages page
        if (WebViewNavigationPolicy.IsInbox(url))
        {
            // Guard: only start the automation chain once (first page load).
            // Subsequent friends reuse the same SPA session — inject script only (no full reload).
            if (_automationStarted) return;
            _automationStarted = true;

            UpdateNotification(Localization.T("Android_ConnectingToTikTok"));
            AppLog("NAVIGATION", "-", "Messages page ready");
            // Wait a bit for the page to fully render, then start automation
            _mainHandler?.PostDelayed(ProcessNextFriend, 3000);
        }
        else if (url.Contains("login"))
        {
            AppLog("NAVIGATION", "-", "TikTok login required");
            // User needs to login
            CompleteService(false, Localization.T("Android_LoginRequired"));
        }
    }

    private void ProcessNextFriend()
    {
        if (_isCancelRequested) return;

        // When "Skip Unreachable Users" is OFF, abort the entire run on any per-user failure
        bool skipUnreachable = _settingsService?.GetSkipUnreachableUsers() ?? false;
        if (!skipUnreachable && _runResult is not null && _runResult.Failed)
        {
            CompleteService(false, string.Format(Localization.T("Android_RunStoppedReason"),
                _runResult.ErrorMessage ?? _runResult.FriendsErrorMessage));
            return;
        }

        if (_friendsToProcess == null || _currentFriendIndex >= _friendsToProcess.Count)
        {
            // Everything that was going to be sent has been. Before calling it done, the chats that
            // reported success are re-opened and checked - a send can report success and still leave no
            // message in the thread, which is the one failure that silently breaks a streak.
            if (ShouldRunDeliveryRescan())
            {
                StartDeliveryRescan();
                return;
            }

            FinishRun();
            return;
        }

        var friend = _friendsToProcess[_currentFriendIndex];

        var logTarget = friend.IsGroup ? $"Group: {friend.DisplayName}" : $"@{friend.Username}";
        AppLog("PROCESS", logTarget, "Starting regular messaging");

        SendCurrentFriendMessage();
    }

    // ── Delivery rescan ──────────────────────────────────────────────────────
    //
    // After the run has sent to everyone, the chats that reported success are opened again and checked.
    // This exists because "the script said success" and "the message is in the thread" are not the same
    // claim: the composer clears for reasons other than a delivered message, so a send can look fine and
    // leave nothing behind. That is the failure that breaks a streak silently, and the only way to catch
    // it is to go back and look.
    //
    // Anything not found is resent once. The pass is deliberately bounded - one scan, one resend - so a
    // disagreement with TikTok's markup can never turn into a send loop.

    /// <summary>
    /// Whether the second pass should run at all.
    /// <para>
    /// Skipped for a rehearsal (nothing was sent), when the user cancelled, and for runs that produced no
    /// successful send to check.
    /// </para>
    /// </summary>
    private bool ShouldRunDeliveryRescan()
    {
        if (_dryRun || _isCancelRequested)
            return false;

        if (_rescanPass >= MaxRescanPasses)
            return false;

        if (_runResult == null)
            return false;

        // Only chats the app believes it delivered to. A failed send is already reported as failed, and
        // resending it here would be a second attempt dressed up as verification.
        return _runResult.FriendResults.Any(r => r.Success && !r.DryRun);
    }

    /// <summary>Builds the queue of chats to re-check and starts the pass.</summary>
    private void StartDeliveryRescan()
    {
        _rescanPass++;
        _isRescanning = true;
        _rescanIndex = 0;
        _rescanFound = 0;
        _rescanAbsent = 0;
        _rescanUnknown = 0;

        // Matched back to the friend list so the resend has a friend to send to. A result whose friend is
        // no longer configured is left out: there is nothing to resend to.
        _rescanQueue = new List<FriendConfig>();

        foreach (var result in DeliveryRescan.Candidates(_runResult!.FriendResults, _resentFriendIds))
        {
            var friend = _friendsToProcess?.FirstOrDefault(f => f.Id == result.FriendId)
                ?? _settingsService?.GetEnabledFriends().FirstOrDefault(f => f.Id == result.FriendId);

            if (friend != null && !_rescanQueue.Any(f => f.Id == friend.Id))
                _rescanQueue.Add(friend);
        }

        AppLog("SYSTEM", "-", $"Verifying delivery for {_rescanQueue.Count} chats");

        UpdateNotification(string.Format(Localization.T("Android_RescanStarting"), _rescanQueue.Count));

        _mainHandler?.PostDelayed(RescanNextFriend, 1500);
    }

    /// <summary>Opens the next chat in the queue and asks the script to look, without sending.</summary>
    private void RescanNextFriend()
    {
        if (_isCancelRequested)
        {
            FinishRun();
            return;
        }

        if (_rescanQueue == null || _rescanIndex >= _rescanQueue.Count)
        {
            EndDeliveryRescan();
            return;
        }

        var friend = _rescanQueue[_rescanIndex];
        var label = friend.IsGroup ? friend.DisplayName : $"@{friend.Username}";

        var result = _runResult?.FriendResults.FirstOrDefault(r => r.FriendId == friend.Id);

        // The text that actually went out, recorded when it was sent. Recomputing it here would break
        // whenever the run used a randomised variant, and the rescan would then search for text that was
        // never sent and report every chat as missing.
        var message = DeliveryRescan.TextToSearch(result);
        if (string.IsNullOrEmpty(message))
        {
            AppLog("RESCAN", label, "No record of what was sent - cannot verify");
            _rescanUnknown++;
            _rescanIndex++;
            _mainHandler?.PostDelayed(RescanNextFriend, 500);
            return;
        }

        // The stretch of time this message is expected in. It opens when the send was attempted, not at the
        // minute the user scheduled: a run takes time, and a message a few minutes late is still delivered.
        // Without a recorded time there is no window, and the rescan falls back to the text alone - a
        // weaker answer it reports as unconfirmed rather than as missing.
        var window = DeliveryRescan.WindowFor(result);
        var windowStart = window?.Start.ToString("O") ?? string.Empty;
        var windowEnd = window?.End.ToString("O") ?? string.Empty;

        UpdateNotification(string.Format(Localization.T("Android_RescanProgress"),
            _rescanIndex + 1, _rescanQueue.Count, label));

        if (_currentFriendTimeoutAction != null)
        {
            _mainHandler?.RemoveCallbacks(_currentFriendTimeoutAction);
            _currentFriendTimeoutAction = null;
        }

        _currentFriendTimeoutAction = () =>
        {
            if (_isCancelRequested) return;
            AppLog("RESCAN", label, "Timed out - treated as unconfirmed");
            OnRescanResult(friend.IsGroup ? friend.DisplayName : friend.Username, "unknown");
        };
        _mainHandler?.PostDelayed(_currentFriendTimeoutAction, FriendTimeoutMs);

        var target = friend.IsGroup ? friend.DisplayName : friend.Username;
        var js = GetFriendMessageScript(target, message, friend.IsGroup, rescanOnly: true,
            windowStart: windowStart, windowEnd: windowEnd);
        _webView?.EvaluateJavascript(js, null);
    }

    /// <summary>Records the rescan's verdict for the chat that was just opened.</summary>
    internal void OnRescanResult(string username, string verdict)
    {
        if (_currentFriendTimeoutAction != null)
        {
            _mainHandler?.RemoveCallbacks(_currentFriendTimeoutAction);
            _currentFriendTimeoutAction = null;
        }

        if (_isCancelRequested) return;

        var friend = _rescanQueue != null && _rescanIndex < _rescanQueue.Count
            ? _rescanQueue[_rescanIndex]
            : null;

        if (friend == null) return;

        var label = friend.IsGroup ? friend.DisplayName : $"@{friend.Username}";
        var result = _runResult?.FriendResults.FirstOrDefault(r => r.FriendId == friend.Id);

        switch (verdict)
        {
            case "found":
                _rescanFound++;
                if (result != null) result.Verified = true;
                AppLog("RESCAN", label, "Message found in chat");
                break;

            case "absent":
                _rescanAbsent++;
                if (result != null) result.Verified = false;
                AppLog("RESCAN", label, "Message NOT found - will resend");
                break;

            default:
                _rescanUnknown++;
                AppLog("RESCAN", label, "Could not read the chat - left as unconfirmed");
                break;
        }

        _rescanIndex++;
        _mainHandler?.PostDelayed(RescanNextFriend, 1500);
    }

    /// <summary>
    /// Ends the pass: resends to the chats whose message was not found, then finishes the run.
    /// </summary>
    private void EndDeliveryRescan()
    {
        AppLog("SYSTEM", "-",
            $"Verification done: {_rescanFound} confirmed, {_rescanAbsent} missing, {_rescanUnknown} unreadable");

        var missing = DeliveryRescan.NeedingResend(_runResult?.FriendResults, _resentFriendIds);

        if (missing.Count == 0)
        {
            FinishRun();
            return;
        }

        // Matched back to friends, because the resend needs somewhere to send.
        var missingFriends = missing
            .Select(r => _friendsToProcess?.FirstOrDefault(f => f.Id == r.FriendId)
                ?? _settingsService?.GetEnabledFriends().FirstOrDefault(f => f.Id == r.FriendId))
            .Where(f => f != null)
            .Select(f => f!)
            .ToList();

        foreach (var friend in missingFriends)
            _resentFriendIds.Add(friend.Id);

        if (missingFriends.Count == 0)
        {
            FinishRun();
            return;
        }

        // The resend reuses the normal send path, one chat at a time, so it inherits the typing, the
        // verification and the durable record rather than duplicating any of it.
        //
        // The run's own list is put aside first: the send path walks _friendsToProcess, and CompleteService
        // uses it to close out chats that never reported. Leaving the resend queue in there would lose the
        // chats from the first pass.
        _runFriends ??= _friendsToProcess;

        _rescanQueue = missingFriends;
        _rescanIndex = 0;
        _isResending = true;

        AppLog("SYSTEM", "-", $"Resending to {missingFriends.Count} chats that were not found");
        UpdateNotification(string.Format(Localization.T("Android_RescanResending"), missingFriends.Count));

        _mainHandler?.PostDelayed(ResendNextFriend, 1500);
    }

    /// <summary>Sends to the next chat that failed verification.</summary>
    private void ResendNextFriend()
    {
        if (_isCancelRequested)
        {
            FinishRun();
            return;
        }

        if (_rescanQueue == null || _rescanIndex >= _rescanQueue.Count)
        {
            _isResending = false;
            _isRescanning = false;
            FinishRun();
            return;
        }

        var friend = _rescanQueue[_rescanIndex];

        // Routed through the same send path the run used. _isResending tells OnMessageResult to update
        // the existing row rather than append a duplicate for the same chat.
        _friendsToProcess = _rescanQueue;
        _currentFriendIndex = _rescanIndex;
        SendCurrentFriendMessage();
    }

    /// <summary>Completes the run with the outcome the rescan settled on.</summary>
    private void FinishRun()
    {
        _isRescanning = false;
        _isResending = false;

        // Put the run's own list back. The resend borrowed it for its queue, and CompleteService reads it
        // to close out any chat that never reported - which has to be the chats the run selected, not the
        // handful the resend was working through.
        if (_runFriends != null)
        {
            _friendsToProcess = _runFriends;
            _runFriends = null;
        }

        var allSucceeded = _runResult?.FriendResults.All(r => r.Success) ?? false;
        var completionMessage = allSucceeded
            ? Localization.T("Android_AllMessagesSent")
            : string.Format(Localization.T("Android_SentOf"),
                _runResult?.FriendResults.Count(r => r.Success) ?? 0,
                _runResult?.FriendResults.Count ?? 0);

        CompleteService(allSucceeded, completionMessage);
    }

    private void SendCurrentFriendMessage()
    {
        if (_isCancelRequested) return;

        var friend = _friendsToProcess![_currentFriendIndex];
        string message;

        string? randomizedMessage = null;

        if (_shuffledNormalMessages != null && _shuffledNormalMessages.Count > 0)
        {
            randomizedMessage = _shuffledNormalMessages[_normalMessageIndex % _shuffledNormalMessages.Count];
            _normalMessageIndex++;
            // Reshuffle when pool is exhausted so the same per-run sequence is not repeated.
            if (_normalMessageIndex >= _shuffledNormalMessages.Count)
            {
                ShuffleList(_shuffledNormalMessages);
                _normalMessageIndex = 0;
            }
        }

        // This friend's own message wins over the randomized pool and the shared message.
        message = MessageResolver.Resolve(
            friend.CustomMessage,
            randomizedMessage,
            _settingsService?.GetMessageText() ?? SettingsService.DefaultMessage);

        // Placeholders are resolved per friend, so one template can address everyone by name.
        message = MessageTemplate.Render(message, friend.Username, friend.DisplayName);

        var displayLabel = friend.IsGroup ? friend.DisplayName : $"@{friend.Username}";
        UpdateNotification(string.Format(Localization.T("Android_Processing"),
            _currentFriendIndex + 1, _friendsToProcess.Count, displayLabel),
            _currentFriendIndex, _friendsToProcess.Count);

        // For groups TikTok has no @handle, so we match the chat header by display name instead.
        var target = friend.IsGroup ? friend.DisplayName : friend.Username;
        if (string.IsNullOrWhiteSpace(target))
        {
            AppLog("FAIL", "-", friend.IsGroup ? "Group name is empty" : "Username is empty");
            _currentFriendIndex++;
            _mainHandler?.PostDelayed(ProcessNextFriend, 1000);
            return;
        }

        _allowSendRetries = true;
        _currentSendText = message;
        _currentSendStartedAt = DateTime.Now;
        var js = GetFriendMessageScript(target, message, friend.IsGroup);

        // Written and flushed to disk before the message goes out, not after it is confirmed. If the
        // system kills the service while this send is in flight, the next run has to be able to tell that
        // a message may already have reached this friend - the result of this send is exactly what a kill
        // takes away. A rehearsal sends nothing, so it records nothing.
        if (!_dryRun)
            PendingSends.MarkAttempted(friend.Id, friend.Username, DateTime.Now);

        if (_currentFriendTimeoutAction != null)
        {
            _mainHandler?.RemoveCallbacks(_currentFriendTimeoutAction);
            _currentFriendTimeoutAction = null;
        }

        _currentFriendTimeoutAction = () =>
        {
            if (_isCancelRequested) return;
            AppLog("TIMEOUT", displayLabel, "Automation watchdog timed out after 75s without callback");
            OnMessageResult(target, false, "Watchdog timeout after 75s");
        };
        _mainHandler?.PostDelayed(_currentFriendTimeoutAction, FriendTimeoutMs);

        _webView?.EvaluateJavascript(js, null);
    }

    private void ShuffleList<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private string GetFriendMessageScript(string target, string message, bool isGroup,
        bool rescanOnly = false, string? windowStart = null, string? windowEnd = null)
    {
        // Escape special characters for JavaScript string literals
        target ??= string.Empty;
        message ??= string.Empty;
        var escapedTarget = target.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"");
        var escapedMessage = message.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"").Replace("\n", "\\n");

        var automationScript = this._baseScript.Replace("[UserName]", escapedTarget);
        automationScript = automationScript.Replace("[Message]", escapedMessage);
        automationScript = automationScript.Replace("[IsGroup]", isGroup ? "true" : "false");

        // If this placeholder ever went unreplaced the script would compare against the literal text and
        // decide against dry run, which is the safe direction: it falls back to normal sending.
        automationScript = automationScript.Replace("[DryRun]", _dryRun ? "true" : "false");

        // The rescan placeholders default to the sending behaviour, so a build where they are missing
        // still sends rather than silently doing nothing.
        automationScript = automationScript.Replace("[RescanOnly]", rescanOnly ? "true" : "false");
        automationScript = automationScript.Replace("[WindowStart]", windowStart ?? string.Empty);
        automationScript = automationScript.Replace("[WindowEnd]", windowEnd ?? string.Empty);
        // Left empty so the script reads the handle from the page, which is where it is known for certain.
        automationScript = automationScript.Replace("[SelfHandle]", string.Empty);

        return automationScript;
    }

    internal void OnMessageResult(string username, bool success, string error)
    {
        if (_currentFriendTimeoutAction != null)
        {
            _mainHandler?.RemoveCallbacks(_currentFriendTimeoutAction);
            _currentFriendTimeoutAction = null;
        }

        if (_isCancelRequested) return;
        if (_friendsToProcess == null || _settingsService == null) return;

        // The JS callback reports the target it was given: a username for DMs, or the
        // display name for groups (groups have no @handle on TikTok).
        var friend = _friendsToProcess.FirstOrDefault(f =>
            (f.IsGroup && f.DisplayName.Equals(username, StringComparison.OrdinalIgnoreCase)) ||
            (!f.IsGroup && f.Username.Equals(username, StringComparison.OrdinalIgnoreCase)));

        if (friend == null)
        {
            // Target reported by JS doesn't match any friend/group in the list. This can happen
            // when TikTok returns a different casing/format. Retry the current friend
            // a few times rather than silently advancing past it.
            AppLog("WARN", $"@{username}",
                "Target from JS callback did not match any entry in the list. Retrying current friend...");
            _failureAttemptsForCurrentFriend++;
            if (_failureAttemptsForCurrentFriend < MaxSendAttemptsPerFriend)
            {
                _mainHandler?.PostDelayed(SendCurrentFriendMessage, 3000);
            }
            else
            {
                AppLog("FAIL", $"@{username}", "Max retries exceeded for unmatched username");
                _failureAttemptsForCurrentFriend = 0;
                _currentFriendIndex++;
                _mainHandler?.PostDelayed(ProcessNextFriend, 3000);
            }
            return;
        }

        var label = friend.IsGroup ? $"Group: {friend.DisplayName}" : $"@{username}";
        if (!success)
        {
            _failureAttemptsForCurrentFriend++;
            if (_allowSendRetries && _failureAttemptsForCurrentFriend < MaxSendAttemptsPerFriend)
            {
                AppLog("RETRY", label,
                    $"Attempt {_failureAttemptsForCurrentFriend}/{MaxSendAttemptsPerFriend}: {error}");
                _mainHandler?.PostDelayed(SendCurrentFriendMessage, 3000);
                return;
            }

            friend.FailureCount++;
            friend.ConsecutiveFailures++;
            AppLog("FAIL", label, error);

            bool skipUnreachable = _settingsService.GetSkipUnreachableUsers();
            if (skipUnreachable && error == UserNotFoundError)
            {
                friend.IsEnabled = false;
                _disabledUsernames.Add(label);
                AppLog("DISABLED", label, "Auto-disabled — not found in chat list");
            }
            _settingsService.UpdateFriend(friend);

            // A definite failure means nothing was sent, so this entry must not block a later retry today.
            PendingSends.MarkFailed(friend.Id, DateTime.Now);

            RecordFriendResult(new FriendMessageResult
            {
                FriendId = friend.Id,
                Username = username,
                Success = false,
                ErrorMessage = error
            });

            // Bumped under the lock so the Dashboard's snapshot never reads a half-added list.
            PublishLiveProgress();
            _failureAttemptsForCurrentFriend = 0;
            _currentFriendIndex++;

            if (_isResending)
            {
                // The resend failed again. The row above already says so, and there is nothing further to
                // try - the pass exists to repair one missed message, not to loop.
                _rescanIndex++;
                UpdateNotification(string.Format(Localization.T("Android_RescanProgress"),
                    Math.Min(_rescanIndex, _rescanQueue?.Count ?? 0), _rescanQueue?.Count ?? 0, label));
                _mainHandler?.PostDelayed(ResendNextFriend, 3000);
                return;
            }

            UpdateNotification(string.Format(Localization.T("Android_ProgressFailed"),
                _currentFriendIndex, _friendsToProcess.Count, label), _currentFriendIndex, _friendsToProcess.Count);
            _mainHandler?.PostDelayed(ProcessNextFriend, 3000);
            return;
        }

        if (_dryRun)
        {
            // Rehearsal: the run reached this chat, which is the whole point of the exercise. Nothing
            // was sent, so LastMessageSent is deliberately left untouched and the row is flagged so it
            // can never be counted as delivered.
            _pendingVerification = null;
            AppLog("DRYRUN", label, "Chat reachable — nothing sent (dry run)");

            _runResult?.FriendResults.Add(new FriendMessageResult
            {
                FriendId = friend.Id,
                Username = username,
                Success = true,
                ErrorMessage = null,
                DryRun = true,
                Verified = null,
            });

            PublishLiveProgress();
            AdvanceToNextFriend(username);
            return;
        }

        friend.SuccessCount++;
        friend.LastMessageSent = DateTime.Now;
        // A success ends the failure streak, which is what makes the health colour in the friends
        // list mean "failing now" rather than "failed at some point".
        friend.ConsecutiveFailures = 0;

        var verification = SendVerification.Classify(scriptReportedSuccess: true, composerCleared: _pendingVerification);
        _pendingVerification = null;

        AppLog(_isResending ? "RESEND" : "SUCCESS", label,
            $"Message sent ({SendVerification.Describe(verification)})");

        _settingsService.UpdateFriend(friend);
        PendingSends.MarkSent(friend.Id, DateTime.Now);

        RecordFriendResult(new FriendMessageResult
        {
            FriendId = friend.Id,
            Username = username,
            Success = true,
            ErrorMessage = null,
            Verified = SendVerification.ToVerifiedFlag(verification),
            // Kept so the rescan searches for what was actually sent, not what would be sent now.
            MessageText = _currentSendText,
            // The window the rescan will look in. The attempt's start, not the scheduled minute.
            SentAt = _currentSendStartedAt,
        });

        PublishLiveProgress();

        if (_isResending)
        {
            // The resend landed. The row it updated is the one from the first attempt, so the history
            // still shows one entry per chat rather than a send followed by a mysterious second one.
            //
            // The friend's own counters are rolled back first: the first attempt already counted this as
            // a success, and counting it again would inflate the lifetime total for a chat that was only
            // ever messaged once.
            friend.SuccessCount = Math.Max(0, friend.SuccessCount - 1);
            _settingsService.UpdateFriend(friend);

            _rescanIndex++;
            UpdateNotification(string.Format(Localization.T("Android_RescanProgress"),
                Math.Min(_rescanIndex, _rescanQueue?.Count ?? 0), _rescanQueue?.Count ?? 0, label));
            _mainHandler?.PostDelayed(ResendNextFriend, 3000);
            return;
        }

        AdvanceToNextFriend(username);
    }

    /// <summary>
    /// Adds a result to the run, or updates the existing row when this is a resend.
    /// <para>
    /// A resend is not a second event: it is the same chat's outcome being corrected. Appending would put
    /// two entries for one friend in the history and make the run look like it messaged them twice.
    /// </para>
    /// </summary>
    private void RecordFriendResult(FriendMessageResult result)
    {
        if (_runResult == null)
            return;

        if (_isResending)
        {
            DeliveryRescan.ApplyResend(_runResult.FriendResults, result);
            return;
        }

        _runResult.FriendResults.Add(result);
    }

    /// <summary>
    /// Signals the UI that the in-flight run has a new result.
    /// <para>
    /// The Dashboard polls once a second, so this does not have to do anything itself; it exists to
    /// hold <see cref="LiveRunLock"/> at the moment a result is appended, which is what keeps a
    /// snapshot taken on the UI thread from seeing a list mid-mutation.
    /// </para>
    /// </summary>
    private void PublishLiveProgress()
    {
        lock (LiveRunLock)
        {
            // No-op by design: the lock is the effect.
        }
    }

    private void AdvanceToNextFriend(string username)
    {
        if (_isResending)
        {
            // Handled at the call site: the resend walks its own queue rather than the run's.
            return;
        }

        var prevFriend = _friendsToProcess != null && _currentFriendIndex < _friendsToProcess.Count
            ? _friendsToProcess[_currentFriendIndex] : null;
        var sentLabel = prevFriend?.IsGroup == true ? prevFriend.DisplayName : $"@{username}";

        _currentFriendIndex++;
        _failureAttemptsForCurrentFriend = 0;
        var completedCount = _currentFriendIndex;
        var totalCount = _friendsToProcess?.Count ?? 0;
        var resultText = string.Format(Localization.T("Android_SentTo"), completedCount, totalCount, sentLabel);
        UpdateNotification(resultText, completedCount, totalCount);

        if (_currentFriendIndex < totalCount)
        {
            AppLog("NAVIGATION", "-", "Next friend — injecting without reloading /messages");
            _mainHandler?.PostDelayed(ProcessNextFriend, 3000);
        }
        else
            _mainHandler?.PostDelayed(ProcessNextFriend, 1000);
    }

    private PendingIntent CreateMainActivityPendingIntent()
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(this, 1, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    private void CompleteSkippedNoNetwork()
    {
        try
        {
            if (_runResult != null && _settingsService != null)
            {
                _runResult.Success = false;
                _runResult.ErrorMessage = Localization.T("Android_SkippedNoNetwork");
                // A skipped run is still a run, so it gets a duration too. The retry below schedules
                // itself deliberately rather than leaning on the interval clock.
                _runResult.Duration = TakeRunDuration();
                _settingsService.AddRunResult(_runResult);
            }

            var attempt = StreakScheduler.TryScheduleRetryOrGiveUp(this, SettingsService.FailureReasonNoNetwork);
            var max = SettingsService.MaxRetriesPerDay;

            string title;
            string body;
            if (attempt > 0)
            {
                title = Localization.T("Android_OfflineTitle");
                body = string.Format(Localization.T("Android_OfflineRetryBody"), attempt, max);
                UpdateNotification(string.Format(Localization.T("Android_OfflineRetryNote"), attempt, max));
            }
            else
            {
                title = Localization.T("Android_OfflineGaveUpTitle");
                body = string.Format(Localization.T("Android_OfflineGaveUpBody"), max);
                UpdateNotification(string.Format(Localization.T("Android_OfflineExhausted"), max));
            }

            var finalNotification = new NotificationCompat.Builder(this, StatusChannelId)
                .SetContentTitle(title)
                .SetContentText(body)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetContentIntent(CreateMainActivityPendingIntent())
                .SetAutoCancel(true)
                .SetPriority(NotificationCompat.PriorityDefault)
                .Build()!;

            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            notificationManager?.Notify(NotificationId + 1, finalNotification);
        }
        finally
        {
            lock (_runLock)
            {
                _isRunning = false;
            }

            CleanupWebView();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    /// <summary>
    /// How long the run took, or null when it never got started. Stopping here keeps a second
    /// completion path from carrying on counting.
    /// </summary>
    private TimeSpan? TakeRunDuration()
    {
        if (_runStopwatch is null)
            return null;

        _runStopwatch.Stop();
        return _runStopwatch.Elapsed;
    }

    private void CompleteService(bool success, string message)
    {
        try
        {
            // Update run result
            if (_runResult != null && _settingsService != null)
            {
                _runResult.Success = success;
                _runResult.ErrorMessage = success ? null : message;
                _runResult.Duration = TakeRunDuration();

                // Close out every chat this run selected but never resolved.
                //
                // A send writes an "attempted" record before it starts. If the run ends before that
                // chat reports back - the user stopped it, the watchdog fired, the service was torn
                // down - the record stays "attempted", and the next run reads that as "may already
                // have been messaged" and skips the chat for the rest of the day. It also never
                // appears in FriendResults, so Retry failed cannot see it either: the chat is simply
                // lost for the day. That is how a run stopped halfway cost the user four friends.
                //
                // The run ended while this code was still in control, so nothing was confirmed sent
                // to them. Recording a failure is the honest state, and it is what makes the chat
                // retryable. The asymmetry is deliberate: a duplicate streak emoji is harmless,
                // whereas a streak silently broken by a skipped chat is the one thing this app exists
                // to prevent. A rehearsal sends nothing, so it records nothing.
                if (!_dryRun && _friendsToProcess != null)
                {
                    foreach (var pending in _friendsToProcess)
                    {
                        if (_runResult.FriendResults.Any(r => r.FriendId == pending.Id))
                            continue;

                        PendingSends.MarkFailed(pending.Id, DateTime.Now);
                        _runResult.FriendResults.Add(new FriendMessageResult
                        {
                            FriendId = pending.Id,
                            Username = pending.Username,
                            Success = false,
                            ErrorMessage = Localization.T("Android_NotReached"),
                        });

                        AppLog("FAIL", $"@{pending.Username}", "Run ended before this chat was reached");
                    }
                }

                _settingsService.AddRunResult(_runResult);
                _settingsService.SetLastRunTime(DateTime.Now);
            }

            // Show completion notification
            var successCount = _runResult?.FriendResults.Count(r => r.Success) ?? 0;
            var totalSent = _runResult?.FriendResults.Count ?? 0;
            var skippedCount = totalSent - successCount;

            // Build human-readable summary including cooldown-skipped friends
            var cooldownNote = _cooldownSkippedCount > 0
                ? string.Format(Localization.T("Android_CooldownNote"), _cooldownSkippedCount)
                : string.Empty;

            string finalText;
            if (success)
            {
                finalText = string.Format(Localization.T("Android_DoneAllSent"), successCount, totalSent, cooldownNote);
            }
            else if (totalSent > 0 && successCount > 0)
            {
                if (_disabledUsernames.Count > 0)
                    finalText = string.Format(Localization.T("Android_DoneSentDisabled"), successCount, totalSent, _disabledUsernames.Count, string.Join(", ", _disabledUsernames), cooldownNote);
                else
                    finalText = string.Format(Localization.T("Android_DoneSentSkipped"), successCount, totalSent, skippedCount, cooldownNote);
            }
            else
            {
                if (_disabledUsernames.Count > 0)
                    finalText = string.Format(Localization.T("Android_DoneSentDisabled"), 0, totalSent, _disabledUsernames.Count, string.Join(", ", _disabledUsernames), cooldownNote);
                else if (totalSent > 0)
                    finalText = string.Format(Localization.T("Android_DoneSentFailed"), 0, totalSent, skippedCount, cooldownNote);
                else
                    finalText = string.Format(Localization.T("Android_StoppedFinal"), message);
            }

            var finalNotification = new NotificationCompat.Builder(this, StatusChannelId)
                .SetContentTitle(Localization.T("Android_NotificationTitle"))
                .SetContentText(finalText)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(finalText))
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetContentIntent(CreateMainActivityPendingIntent())
                .SetAutoCancel(true)
                .SetPriority(NotificationCompat.PriorityDefault)
                .Build()!;

            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            notificationManager?.Notify(NotificationId + 1, finalNotification);

            // Re-arm the scheduler if scheduling is enabled. Either:
            //   - everything succeeded → normal next-run slot, retry counter reset
            //   - anything failed → 1-hour retry (up to MaxRetriesPerDay), then normal slot
            if (_settingsService?.IsScheduled() == true)
            {
                bool allSucceeded = success
                    && (_runResult?.FriendResults.Count == 0
                        || _runResult.FriendResults.All(r => r.Success));

                if (allSucceeded)
                {
                    _settingsService.ResetTodayRetryCount();
                    _settingsService.SetLastRunFailed(false, null);
                    StreakScheduler.ScheduleNextRun(this);
                }
                else
                {
                    var attempt = StreakScheduler.TryScheduleRetryOrGiveUp(this, SettingsService.FailureReasonSendError);
                    if (attempt > 0)
                        AppLog("SYSTEM", "-", $"Run had errors — scheduled hourly retry {attempt}/{SettingsService.MaxRetriesPerDay}");
                    else
                        AppLog("SYSTEM", "-", $"Run had errors — retry budget exhausted, normal next-run slot scheduled");
                }
            }

            AppLog("SYSTEM", "-", $"Run complete: {(success ? "Success" : message)}");
        }
        finally
        {
            // ── Clear the run-level mutex on ALL exit paths ──
            lock (_runLock)
            {
                _isRunning = false;
            }

            // The run is over and has been written to history, so the live copy must not linger -
            // the Dashboard would otherwise keep showing it instead of the recorded history.
            lock (LiveRunLock)
            {
                LiveRun = null;
            }

            CleanupWebView();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    /// <summary>
    /// WebView client for handling page events
    /// </summary>
    [Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
    private class StreakWebViewClient : WebViewClient
    {
        private readonly StreakService _service;

        public StreakWebViewClient(StreakService service)
        {
            _service = service;
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            if (!string.IsNullOrEmpty(url))
            {
                _service.OnPageLoaded(url);
            }
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (request?.Url is not null)
            {
                if ((request.Url.EncodedSchemeSpecificPart ?? "").StartsWith("//aweme"))
                {
                    return true;
                }
            }
            // Allow navigation within TikTok
            return false;
        }
    }

    /// <summary>
    /// JavaScript interface for communication from WebView
    /// </summary>
    [Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
    private class StreakJsInterface : Java.Lang.Object
    {
        private readonly StreakService _service;

        public StreakJsInterface(StreakService service)
        {
            _service = service;
        }

        [JavascriptInterface]
        [Export("onMessageSent")]
        public void OnMessageSent(string username, bool success, string error)
        {
            _service._mainHandler?.Post(() => _service.OnMessageResult(username, success, error));
        }

        /// <summary>
        /// Reports whether the composer was empty after sending. Called by the script just before
        /// <see cref="OnMessageSent"/>, so the value is already waiting when the result arrives.
        /// </summary>
        [JavascriptInterface]
        [Export("reportVerification")]
        public void ReportVerification(bool composerCleared)
        {
            _service._mainHandler?.Post(() => _service._pendingVerification = composerCleared);
        }

        /// <summary>
        /// The rescan's verdict for the chat it just opened: "found", "absent" or "unknown".
        /// </summary>
        [JavascriptInterface]
        [Export("onRescanResult")]
        public void OnRescanResult(string username, string verdict)
        {
            _service._mainHandler?.Post(() => _service.OnRescanResult(username, verdict));
        }

        [JavascriptInterface]
        [Export("log")]
        public void Log(string message)
        {
            // Messages from the injected script carry no phase, so they keep the plain layout. They are
            // persisted too, because the script is where a run tends to stall.
            StreakService.AppendLogEntry($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }
}

