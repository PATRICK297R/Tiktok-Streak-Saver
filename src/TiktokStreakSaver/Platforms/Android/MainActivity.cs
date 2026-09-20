using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Microsoft.Maui.ApplicationModel;
using TiktokStreakSaver.Services;

namespace TiktokStreakSaver
{
    [Activity(Theme = "@style/App.StartingTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    [Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
    public class MainActivity : MauiAppCompatActivity
    {
        private const int NotificationPermissionRequestCode = 1001;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            
            // Create notification channel on app start
            CreateNotificationChannel();
            
            // Request notification permission for Android 13+
            RequestNotificationPermission();

            // A cold start from a notification carries the same extras a warm one does.
            HandleIntent(Intent);
        }

        /// <summary>
        /// Called instead of <see cref="OnCreate"/> when the app is already running, which is the normal
        /// way a chat-import notification is tapped: the activity is in the background and gets resumed
        /// with the new intent.
        /// </summary>
        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);

            // Handed to the framework as well, so what the activity reports as its intent is the one that
            // just arrived rather than the one it was created with.
            if (intent is not null)
                Intent = intent;

            HandleIntent(intent);
        }

        /// <summary>
        /// Picks up the "open the import page" request a chat-import notification carries.
        /// <para>
        /// Only recorded here, never navigated from: this runs before the shell exists on a cold start, so
        /// the request waits until the shell can act on it. The attempt dispatched below is for the other
        /// case - the app already running, where nothing else is going to navigate and the page would
        /// otherwise not open until some later navigation happened to collect the request.
        /// </para>
        /// </summary>
        private static void HandleIntent(Intent? intent)
        {
            if (intent?.GetBooleanExtra(AppConstants.ExtraOpenContactImport, false) != true)
                return;

            // Consumed so it cannot fire twice. The same intent is handed back after a configuration
            // change, which would otherwise re-open the page on every rotation.
            intent.RemoveExtra(AppConstants.ExtraOpenContactImport);

            ContactImportLaunch.Request();

            MainThread.BeginInvokeOnMainThread(() => _ = AppShell.OpenImportPageAsync());
        }

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channelId = "streak_service_channel";
                var channelName = "Streak Service";
                var channelDescription = "Notifications for TikTok streak automation";
                var importance = NotificationImportance.Low;
                
                var channel = new NotificationChannel(channelId, channelName, importance)
                {
                    Description = channelDescription
                };
                
                var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
                notificationManager?.CreateNotificationChannel(channel);
            }
        }

        private void RequestNotificationPermission()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                if (ContextCompat.CheckSelfPermission(this, Android.Manifest.Permission.PostNotifications) 
                    != Permission.Granted)
                {
                    ActivityCompat.RequestPermissions(this, 
                        new[] { Android.Manifest.Permission.PostNotifications }, 
                        NotificationPermissionRequestCode);
                }
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            Platform.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        }
    }
}
