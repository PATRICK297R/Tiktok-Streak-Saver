namespace TiktokStreakSaver.Pages;

using TiktokStreakSaver.Services;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class PrivacyPage : ContentPage
{
    private const string PolicyUrl = "https://github.com/PATRICK297R/Tiktok-Streak-Saver/blob/main/PRIVACY.md";
    private const string IssuesUrl = "https://github.com/PATRICK297R/Tiktok-Streak-Saver/issues";

    public PrivacyPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        this.Opacity = 0;
        this.TranslationY = 12;
        await Task.WhenAll(
            this.FadeTo(1, 280, Easing.SinInOut),
            this.TranslateTo(0, 0, 280, Easing.SinInOut));
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Navigation.PopAsync();
    }

    private async void OnViewOnGitHubClicked(object? sender, EventArgs e)
    {
        try
        {
            await Launcher.OpenAsync(PolicyUrl);
        }
        catch (Exception ex)
        {
            await DisplayAlert(Localization.T("Pv_OpenLinkTitle"),
                string.Format(Localization.T("Pv_OpenLinkError"), ex.Message), Localization.T("Common_OK"));
        }
    }

    private async void OnContactGitHubClicked(object? sender, EventArgs e)
    {
        try
        {
            await Launcher.OpenAsync(IssuesUrl);
        }
        catch (Exception ex)
        {
            await DisplayAlert(Localization.T("Pv_OpenLinkTitle"),
                string.Format(Localization.T("Pv_OpenLinkError"), ex.Message), Localization.T("Common_OK"));
        }
    }
}
