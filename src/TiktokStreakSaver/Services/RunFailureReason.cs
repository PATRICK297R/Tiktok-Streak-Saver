namespace TiktokStreakSaver.Services;

/// <summary>
/// Turns the failure reason stored on a run into something the user can read.
/// <para>
/// The reasons are written by the automation script, by the Android service, and by older builds - all of
/// them in English, and all of them stored as-is on the run result. The history screen used to print
/// whatever was stored, so a phone set to Indonesian showed an English sentence next to an Indonesian
/// label. This is the translation step, and it lives here rather than in the page so the mapping can be
/// checked by the tests.
/// </para>
/// <para>
/// Matching is on a distinctive fragment rather than the whole string, because the stored text often
/// carries a detail on the end ("Exception in chat traversal: ..."). A reason that matches nothing is
/// returned unchanged: an untranslated sentence is a worse-looking but truthful outcome, and inventing a
/// generic "something failed" would hide the detail the user needs to act on.
/// </para>
/// </summary>
public static class RunFailureReason
{
    /// <summary>
    /// The fragments to look for, paired with the key that says them in the interface language, in the
    /// order they are tried.
    /// <para>
    /// Order matters where one reason contains another: "No chat items found" must be tested before the
    /// broader "chat", or it would be read as a chat-panel failure.
    /// </para>
    /// </summary>
    private static readonly (string Fragment, string Key)[] Patterns =
    {
        // Written by the script's watchdog, and by the native side when a send never reported back.
        ("timed out", "Hist_ErrorTimeout"),
        ("watchdog timeout", "Hist_ErrorTimeout"),

        // The chat the run wanted is not in TikTok's list any more - renamed, blocked, or deleted.
        ("user not found", "Hist_ErrorUserNotFound"),

        // The composer: typing into it, or finding it at all.
        ("could not type", "Hist_ErrorComposer"),
        ("failed to type", "Hist_ErrorComposer"),
        ("message input not found", "Hist_ErrorComposer"),

        // The chat panel itself, which is a page-level problem rather than a per-chat one.
        ("no chat items found", "Hist_ErrorNoChats"),
        ("chat panel", "Hist_ErrorChatPanel"),

        // Anything the script threw. Checked late so a more specific reason wins.
        ("exception in chat traversal", "Hist_ErrorScript"),
        ("script error", "Hist_ErrorScript"),

        // A chat the run never got to. Written in the interface language already, so it usually matches
        // the translated form and is returned as-is; the English form covers runs stored before that.
        ("run ended before", "Android_NotReached"),
        ("not reached", "Android_NotReached"),
    };

    /// <summary>
    /// The readable form of a stored reason, in the interface language. Empty stays empty; a reason with
    /// no known meaning is returned unchanged.
    /// </summary>
    public static string Describe(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return string.Empty;

        foreach (var (fragment, key) in Patterns)
        {
            if (reason.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return AppStrings.T(key);
        }

        return reason;
    }
}
