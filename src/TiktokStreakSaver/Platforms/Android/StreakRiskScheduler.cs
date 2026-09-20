using Android.App;
using Android.Content;
using Android.OS;
using TiktokStreakSaver;
using TiktokStreakSaver.Platforms.Android.Receivers;
using TiktokStreakSaver.Services;

namespace TiktokStreakSaver.Platforms.Android;

/// <summary>
/// Schedules the daily streak-risk check.
/// <para>
/// Separate from <see cref="StreakScheduler"/> on purpose: that one arms the run itself, this one arms
/// a reminder that fires late in the day to warn when the run has not managed to send everything. They
/// use different request codes so one cannot cancel the other.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class StreakRiskScheduler
{
    private const int RiskAlarmRequestCode = 1002;

    /// <summary>
    /// Arms the next daily risk check. Cancels any existing one when the feature is off, so toggling it
    /// off in Profile takes effect immediately rather than at the next reboot.
    /// </summary>
    public static void ScheduleNext(Context context)
    {
        var settings = new SettingsService();

        if (!settings.GetStreakRiskAlerts())
        {
            Cancel(context);
            return;
        }

        var nextCheck = ScheduleCalculator.NextFixedDaily(settings.GetStreakRiskWarningHour(), 0, DateTime.Now);
        ScheduleAt(context, nextCheck);
    }

    public static void Cancel(Context context)
    {
        try
        {
            var alarmManager = (AlarmManager?)context.GetSystemService(Context.AlarmService);
            if (alarmManager == null) return;

            var intent = new Intent(context, typeof(StreakRiskReceiver));
            intent.SetAction(StreakRiskReceiver.ActionStreakRiskAlarm);

            var pendingIntent = PendingIntent.GetBroadcast(
                context,
                RiskAlarmRequestCode,
                intent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            if (pendingIntent != null)
                alarmManager.Cancel(pendingIntent);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StreakRiskScheduler.Cancel: {ex.Message}");
        }
    }

    public static void ScheduleAt(Context context, DateTime triggerTime)
    {
        var alarmManager = (AlarmManager?)context.GetSystemService(Context.AlarmService);
        if (alarmManager == null) return;

        var intent = new Intent(context, typeof(StreakRiskReceiver));
        intent.SetAction(StreakRiskReceiver.ActionStreakRiskAlarm);

        var pendingIntent = PendingIntent.GetBroadcast(
            context,
            RiskAlarmRequestCode,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        if (pendingIntent == null) return;

        var triggerAtMillis = new DateTimeOffset(triggerTime).ToUnixTimeMilliseconds();

        // Best-effort, exactly like the run alarm: a reminder must never be the reason the app throws.
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
            {
                if (alarmManager.CanScheduleExactAlarms())
                    alarmManager.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAtMillis, pendingIntent);
                else
                    alarmManager.SetAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAtMillis, pendingIntent);
            }
            else if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                alarmManager.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAtMillis, pendingIntent);
            }
            else
            {
                // Inexact below API 23: a late reminder is still useful, a crashed app is not.
                alarmManager.Set(AlarmType.RtcWakeup, triggerAtMillis, pendingIntent);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StreakRiskScheduler: exact alarm unavailable: {ex.Message}");
            try
            {
                alarmManager.Set(AlarmType.RtcWakeup, triggerAtMillis, pendingIntent);
            }
            catch (Exception fallbackEx)
            {
                System.Diagnostics.Debug.WriteLine($"StreakRiskScheduler: alarm failed too: {fallbackEx.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine($"StreakRiskScheduler: next risk check at {triggerTime}");
    }
}
