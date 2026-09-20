using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using TiktokStreakSaver;
using TiktokStreakSaver.Platforms.Android.Receivers;
using TiktokStreakSaver.Platforms.Android.Services;
using TiktokStreakSaver.Services;

namespace TiktokStreakSaver.Platforms.Android;

/// <summary>
/// Manages alarm scheduling for the streak service
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class StreakScheduler
{
    private const int AlarmRequestCode = 1001;

    /// <summary>
    /// Schedule the next streak run based on settings
    /// </summary>
    public static void ScheduleNextRun(Context context)
    {
        var settingsService = new SettingsService();

        // Same calculation as SettingsService.GetNextRunTime, so the alarm that fires and the
        // "Next Scheduled Run" label on the Dashboard can no longer disagree.
        var nextRunTime = ScheduleCalculator.Next(
            settingsService.GetUseFixedTime(),
            settingsService.GetFixedDailyTimes(),
            settingsService.GetIntervalHours(),
            settingsService.GetLastRunTime(),
            DateTime.Now);

        ScheduleAt(context, nextRunTime);
        settingsService.SetScheduled(true);

        // Arm the late-day warning alongside the run itself. Doing it here covers every existing
        // call site (login, profile changes, boot, run completion) without each needing to know about it.
        StreakRiskScheduler.ScheduleNext(context);
    }

    /// <summary>
    /// When the device is offline, retry the streak check in one hour without changing last-run time.
    /// </summary>
    public static void ScheduleRetryInOneHour(Context context)
    {
        ScheduleAt(context, DateTime.Now.AddHours(1));
        new SettingsService().SetScheduled(true);
    }

    /// <summary>
    /// Either schedule a 1-hour retry (if we haven't exhausted the daily retry budget yet)
    /// or fall back to the normal next-run slot. Tags <see cref="SettingsService.SetLastRunFailed"/>
    /// so listeners (network change, battery low) know whether a recovery attempt is wanted.
    /// </summary>
    /// <param name="context">Android context used to schedule the alarm.</param>
    /// <param name="reason">Why the run failed (e.g. <see cref="SettingsService.FailureReasonNoNetwork"/>).</param>
    /// <returns>
    /// The 1-based retry attempt number when a retry was scheduled, or 0 when the daily
    /// budget is exhausted and the next normal-slot run was scheduled instead.
    /// </returns>
    public static int TryScheduleRetryOrGiveUp(Context context, string reason)
    {
        var settings = new SettingsService();
        var attempt = settings.IncrementTodayRetryCount();

        if (attempt <= SettingsService.MaxRetriesPerDay)
        {
            ScheduleRetryInOneHour(context);
            settings.SetLastRunFailed(true, reason);
            System.Diagnostics.Debug.WriteLine(
                $"StreakScheduler: hourly retry {attempt}/{SettingsService.MaxRetriesPerDay} scheduled (reason: {reason})");
            return attempt;
        }

        // Daily retry budget exhausted — clear the recovery flag so listeners
        // don't keep restarting the service, and re-arm the normal cadence.
        settings.SetLastRunFailed(false, null);
        ScheduleNextRun(context);
        System.Diagnostics.Debug.WriteLine(
            $"StreakScheduler: retry budget exhausted ({SettingsService.MaxRetriesPerDay}), falling back to normal next-run slot");
        return 0;
    }

    /// <summary>
    /// Schedule a run at a specific time
    /// </summary>
    public static void ScheduleAt(Context context, DateTime triggerTime)
    {
        var alarmManager = (AlarmManager?)context.GetSystemService(Context.AlarmService);
        if (alarmManager == null) return;

        var intent = new Intent(context, typeof(AlarmReceiver));
        intent.SetAction(AlarmReceiver.ActionStreakAlarm);

        var pendingIntent = PendingIntent.GetBroadcast(
            context,
            AlarmRequestCode,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
        );

        if (pendingIntent == null) return;

        // Convert to milliseconds since epoch
        var triggerAtMillis = new DateTimeOffset(triggerTime).ToUnixTimeMilliseconds();

        // Use exact alarm for precise timing.
        // This runs from the post-login flow, which is an async void event handler, so an exception
        // thrown here would reach the Android main looper and close the app. Scheduling the alarm is
        // best-effort: guard every call and degrade to a less exact alarm instead of failing.
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
            {
                // Android 12+ requires checking for exact alarm permission
                if (alarmManager.CanScheduleExactAlarms())
                {
                    alarmManager.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAtMillis, pendingIntent);
                }
                else
                {
                    // Fall back to inexact alarm, but request permission
                    alarmManager.SetAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAtMillis, pendingIntent);
                }
            }
            else if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                alarmManager.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAtMillis, pendingIntent);
            }
            else if (Build.VERSION.SdkInt >= BuildVersionCodes.Kitkat)
            {
                alarmManager.SetExact(AlarmType.RtcWakeup, triggerAtMillis, pendingIntent);
            }
            else
            {
                alarmManager.Set(AlarmType.RtcWakeup, triggerAtMillis, pendingIntent);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StreakScheduler: exact alarm unavailable, falling back: {ex.Message}");
            try
            {
                alarmManager.SetAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAtMillis, pendingIntent);
            }
            catch (Exception fallbackEx)
            {
                // Nothing left to try; the next app launch reschedules.
                System.Diagnostics.Debug.WriteLine($"StreakScheduler: inexact alarm failed too: {fallbackEx.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine($"StreakScheduler: Scheduled next run at {triggerTime}");
    }

    /// <summary>
    /// Cancel any scheduled alarm
    /// </summary>
    public static void CancelSchedule(Context context)
    {
        var alarmManager = (AlarmManager?)context.GetSystemService(Context.AlarmService);
        if (alarmManager == null) return;

        var intent = new Intent(context, typeof(AlarmReceiver));
        intent.SetAction(AlarmReceiver.ActionStreakAlarm);

        var pendingIntent = PendingIntent.GetBroadcast(
            context,
            AlarmRequestCode,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
        );

        if (pendingIntent != null)
        {
            alarmManager.Cancel(pendingIntent);
        }

        var settingsService = new SettingsService();
        settingsService.SetScheduled(false);

        System.Diagnostics.Debug.WriteLine("StreakScheduler: Schedule cancelled");
    }

    /// <summary>
    /// Run the service immediately. Returns false if automation is already running.
    /// </summary>
    public static bool RunNow(Context context)
    {
        if (StreakService.IsRunning)
            return false;

        var settingsService = new SettingsService();
        if (settingsService.IsScheduled())
        {
            CancelSchedule(context);
            settingsService.SetScheduled(true);
        }

        var serviceIntent = new Intent(context, typeof(StreakService));

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            context.StartForegroundService(serviceIntent);
        else
            context.StartService(serviceIntent);

        return true;
    }

    /// <summary>
    /// Request the running StreakService to stop gracefully.
    /// </summary>
    public static void StopService(Context context)
    {
        var serviceIntent = new Intent(context, typeof(StreakService));
        serviceIntent.SetAction("STOP_SERVICE");

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            context.StartForegroundService(serviceIntent);
        else
            context.StartService(serviceIntent);
    }

    /// <summary>
    /// Check if exact alarms can be scheduled (Android 12+)
    /// </summary>
    public static bool CanScheduleExactAlarms(Context context)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.S)
        {
            return true;
        }

        var alarmManager = (AlarmManager?)context.GetSystemService(Context.AlarmService);
        return alarmManager?.CanScheduleExactAlarms() ?? false;
    }

    /// <summary>
    /// Open settings to allow exact alarms (Android 12+)
    /// </summary>
    public static void RequestExactAlarmPermission(Context context)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
        {
            var intent = new Intent(Settings.ActionRequestScheduleExactAlarm);
            var packageName = context.PackageName;
            if (!string.IsNullOrEmpty(packageName))
                intent.SetData(global::Android.Net.Uri.Parse($"package:{packageName}"));
            intent.SetFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
    }

    /// <summary>
    /// Request to ignore battery optimizations
    /// </summary>
    public static void RequestBatteryOptimizationExemption(Context context)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            var powerManager = (PowerManager?)context.GetSystemService(Context.PowerService);
            var packageName = context.PackageName;

            if (powerManager != null && packageName != null && !powerManager.IsIgnoringBatteryOptimizations(packageName))
            {
                var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations);
                intent.SetData(global::Android.Net.Uri.Parse($"package:{packageName}"));
                intent.SetFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
            }
        }
    }

    /// <summary>
    /// Check if app is exempted from battery optimization
    /// </summary>
    public static bool IsIgnoringBatteryOptimizations(Context context)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            var powerManager = (PowerManager?)context.GetSystemService(Context.PowerService);
            var packageName = context.PackageName;

            return powerManager?.IsIgnoringBatteryOptimizations(packageName) ?? false;
        }
        return true;
    }
}









