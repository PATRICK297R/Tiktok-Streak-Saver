using TiktokStreakSaver.Services;
using TiktokStreakSaver.Services.Storage;

namespace TiktokStreakSaver.Pages;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class IosOnboardingPage : ContentPage
{
    private int _step;

    public IosOnboardingPage()
    {
        InitializeComponent();
        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        _step = step;
        SecondaryButton.IsVisible = step > 0 && step < 4;
        SecondaryButton.Text = step == 1 ? Localization.T("Ios_ReadFullDetails") : Localization.T("Ios_SkipForNow");

        switch (step)
        {
            case 0:
                StepTitleLabel.Text = Localization.T("Ios_WelcomeTitle");
                StepBodyLabel.Text = Localization.T("Ios_WelcomeBody");
                PrimaryButton.Text = Localization.T("About_Continue");
                break;
            case 1:
                StepTitleLabel.Text = Localization.T("Ios_LimitsTitle");
                StepBodyLabel.Text = Localization.T("Ios_LimitsBody");
                PrimaryButton.Text = Localization.T("Ios_UnderstandContinue");
                break;
            case 2:
                StepTitleLabel.Text = Localization.T("Ios_SetUpShortcuts");
                StepBodyLabel.Text = Localization.T("Ios_SetUpShortcutsBody");
                PrimaryButton.Text = Localization.T("Ios_OpenSetupGuide");
                break;
            case 3:
                StepTitleLabel.Text = Localization.T("Ios_AltStoreTitle");
                StepBodyLabel.Text = Localization.T("Ios_AltStoreBody");
                PrimaryButton.Text = Localization.T("Ios_Understand");
                break;
            case 4:
                StepTitleLabel.Text = Localization.T("Ios_NotificationsTitle");
                StepBodyLabel.Text = Localization.T("Ios_NotificationsBody");
                PrimaryButton.Text = Localization.T("Ios_EnableNotifications");
                break;
            default:
                CompleteOnboarding();
                break;
        }
    }

    private async void OnPrimaryClicked(object? sender, EventArgs e)
    {
        if (_step == 2)
        {
            var tutorial = new IosShortcutTutorialPage();
            tutorial.Completed += (_, _) => ShowStep(3);
            await Navigation.PushAsync(tutorial);
            return;
        }

        if (_step == 4)
        {
#if IOS
            await Platforms.iOS.Services.IosNotificationService.RequestPermissionAsync();
#endif
            ShowStep(5);
            return;
        }

        ShowStep(_step + 1);
    }

    private async void OnSkipClicked(object? sender, EventArgs e)
    {
        if (_step == 1)
        {
            await Navigation.PushAsync(new IosLimitationsPage());
            return;
        }

        ShowStep(_step + 1);
    }

    private void CompleteOnboarding()
    {
        AppStorageProvider.Current.SetBool(AppConstants.IosOnboardingCompleteKey, true);
        if (Application.Current?.Windows.FirstOrDefault() is { } window)
            window.Page = new AppShell();
    }
}
