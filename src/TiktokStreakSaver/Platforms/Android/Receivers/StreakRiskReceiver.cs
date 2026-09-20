using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using TiktokStreakSaver;
using TiktokStreakSaver.Platforms.Android.Services;
using TiktokStreakSaver.Services;

namespace TiktokStreakSaver.Platforms.Android.Receivers;

/// <summary>
/// Daily check that warns the user while there is still time to save today's streaks.
/// <para>
/// The app only ever reported <em>after</em> a run finished. A day where the run never fired, or fired
/// and failed, could therefore pass silently and the user would find out once the streak was already
/// gone. This fires late in the day, and stays quiet whenever everything has in fact been sent.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
[BroadcastReceiver(Name = AppConstants.PackageName + ".Receivers.StreakRiskReceiver", Enabled = true, Exported = false)]
public class StreakRiskReceiver : BroadcastReceiver
{
    public const string ActionStreakRiskAlarm = AppConstants.ActionStreakRiskAlarm;

    private const string ChannelId = "streak_risk_channel";
    private const string ChannelName = "Streak reminders";
    private const int NotificationId = 9001;

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null) return;
        if (intent.Action != ActionStreakRiskAlarm) return;

        try
        {
            var settings = new SettingsService();

            if (!settings.GetStreakRiskAlerts())
                return;

            var risk = StreakRiskEvaluator.Evaluate(
                settings.GetRunHistory(),
                settings.GetEnabledFriends().Count,
                DateTime.Now,
                settings.GetStreakRiskWarningHour());

            // One warning a day: a reminder that repeats is a reminder that gets ignored.
            if (risk.IsAtRisk && !settings.WasRiskAlertSentToday())
            {
                PostWarning(context, risk.Summary);
                settings.MarkRiskAlertSentToday();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StreakRiskReceiver.OnReceive: {ex.Message}");
        }
        finally
        {
            // Re-arm unconditionally so tomorrow is covered even when today's check failed or was not
            // needed. A reminder must not be lost because of a transient error.
            try
            {
                StreakRiskScheduler.ScheduleNext(context);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"StreakRiskReceiver re-arm: {ex.Message}");
            }
        }
    }

    private static void PostWarning(Context context, string summary)
    {
        try
        {
            var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            if (manager == null) return;

            EnsureChannel(manager);

            var builder = new NotificationCompat.Builder(context, ChannelId)
                .SetContentTitle("Streaks still need a message")
                .SetContentText(summary)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(summary))
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetContentIntent(CreateLaunchIntent(context))
                .SetAutoCancel(true)
                .SetPriority(NotificationCompat.PriorityHigh)
                .SetCategory(NotificationCompat.CategoryReminder);

            manager.Notify(NotificationId, builder.Build()!);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StreakRiskReceiver.PostWarning: {ex.Message}");
        }
    }

    private static void EnsureChannel(NotificationManager manager)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        if (manager.GetNotificationChannel(ChannelId) != null) return;

        // Default importance so the reminder actually surfaces; it is infrequent by design.
        var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Default)
        {
            Description = "Warns when today's streaks have not been sent yet",
        };

        manager.CreateNotificationChannel(channel);
    }

    private static PendingIntent? CreateLaunchIntent(Context context)
    {
        var launchIntent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? string.Empty);

        if (launchIntent == null)
            return null;

        launchIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);

        return PendingIntent.GetActivity(
            context,
            NotificationId,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }
}
