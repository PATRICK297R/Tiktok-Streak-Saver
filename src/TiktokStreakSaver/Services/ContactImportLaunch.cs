namespace TiktokStreakSaver.Services;

/// <summary>
/// A request, made by tapping a chat-import notification, to open the import page.
/// <para>
/// The tap is delivered to the activity, which is too early to navigate from: on a cold start the shell
/// does not exist yet, and on a warm one the activity is being resumed rather than navigated. So the
/// request is recorded here and collected by the shell once it is in a state to act on it.
/// </para>
/// <para>
/// Collected rather than read: taking it clears it, so the same tap cannot open the page twice. That is
/// the one failure this class exists to prevent, and it is why the state lives here and not in a field on
/// the activity - the activity is recreated for a configuration change and is handed the same intent back,
/// so a field there would re-open the page on every rotation.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class ContactImportLaunch
{
    private static readonly object Gate = new();
    private static bool _requested;

    /// <summary>Records that the import page should be opened as soon as the shell can.</summary>
    public static void Request()
    {
        lock (Gate)
            _requested = true;
    }

    /// <summary>
    /// Collects the request, if there is one.
    /// <para>
    /// Returns true at most once per request, which is what makes it safe for the two places that may act
    /// on it - the resume that follows the tap and the shell's own first navigation - to both try.
    /// </para>
    /// </summary>
    public static bool TryTake()
    {
        lock (Gate)
        {
            if (!_requested)
                return false;

            _requested = false;
            return true;
        }
    }

    /// <summary>True while a request is waiting to be collected.</summary>
    public static bool IsPending
    {
        get
        {
            lock (Gate)
                return _requested;
        }
    }

    /// <summary>Forgets a request that will not be acted on, such as one for a page that is already open.</summary>
    public static void Clear()
    {
        lock (Gate)
            _requested = false;
    }
}
