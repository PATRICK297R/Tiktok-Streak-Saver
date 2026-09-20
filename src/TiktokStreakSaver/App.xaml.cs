namespace TiktokStreakSaver;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        // The stored language is read once, before any window is built, so the first screen already draws
        // in the right language rather than switching a moment after it appears.
        Services.Localization.Current.Load();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
#if IOS
        if (!Services.Storage.AppStorageProvider.Current.GetBool(AppConstants.IosOnboardingCompleteKey))
            return new Window(new NavigationPage(new Pages.IosOnboardingPage()));
#endif
        return new Window(new AppShell());
    }
}
