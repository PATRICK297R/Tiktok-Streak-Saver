namespace TiktokStreakSaver;

using TiktokStreakSaver.Services;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class AppShell : Shell
{
    /// <summary>
    /// Set once the shell has navigated of its own accord, which is the point from which it can be asked
    /// to navigate somewhere else.
    /// <para>
    /// This is what separates the two ways a chat-import notification can be tapped. When the app is
    /// already running this is long since true and the page opens immediately; on a cold start the shell
    /// has not navigated yet, and asking it to would either throw or be undone by the navigation it was
    /// already in the middle of.
    /// </para>
    /// </summary>
    private static bool _ready;

    public AppShell()
    {
        InitializeComponent();

        Routing.RegisterRoute(nameof(LoginPage), typeof(LoginPage));

        // The shell is the only thing that knows when it can navigate, and on a cold start the tap arrives
        // long before that. Acting on its first navigation covers that case without anyone needing to
        // guess how long the shell takes to come up.
        Navigated += OnShellNavigated;
    }

    private void OnShellNavigated(object? sender, ShellNavigatedEventArgs e)
    {
        _ready = true;

        if (!ContactImportLaunch.IsPending)
            return;

        // Dispatched rather than run here: this fires inside a navigation of the shell's own, and starting
        // another one from within it is what leaves two navigations racing each other.
        MainThread.BeginInvokeOnMainThread(() => _ = OpenImportPageAsync());
    }

    /// <summary>
    /// Opens the chat import page, where the results of a finished import are waiting.
    /// <para>
    /// The Friends tab is selected first because that is where importing lives, so going back from the page
    /// lands somewhere that makes sense rather than on whichever tab happened to be open.
    /// </para>
    /// <para>
    /// Safe to call from more than one place: the request is collected with
    /// <see cref="ContactImportLaunch.TryTake"/>, so only the first caller acts, and a page already on the
    /// stack is left alone rather than opened a second time.
    /// </para>
    /// </summary>
    public static async Task OpenImportPageAsync()
    {
        try
        {
            if (!_ready || Shell.Current is not { } shell)
                return;

            if (!ContactImportLaunch.TryTake())
                return;

            // Already there: the page reads the scan itself, and a second copy would be a second page
            // reading the same results.
            if (shell.Navigation?.NavigationStack?.Any(page => page is Pages.ImportContactsPage) == true)
                return;

            await shell.GoToAsync("//FriendsPage");
            await shell.Navigation.PushAsync(new Pages.ImportContactsPage());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AppShell: could not open the import page — {ex}");
        }
    }
}
