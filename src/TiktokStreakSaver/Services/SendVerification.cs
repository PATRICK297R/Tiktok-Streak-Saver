namespace TiktokStreakSaver.Services;

/// <summary>How much confidence we have that a message really went out.</summary>
public enum SendOutcome
{
    /// <summary>The send was rejected, or the automation reported an error.</summary>
    Failed,

    /// <summary>The composer cleared after sending, which is the signal we trust.</summary>
    Confirmed,

    /// <summary>The script reported success but the composer did not clear, or could not be checked.</summary>
    Unverified,
}

/// <summary>
/// Turns the raw signals from the automation script into a send outcome.
/// <para>
/// The script used to call <c>reportSuccess()</c> unconditionally one second after clicking send, so
/// "success" only ever meant "we clicked something and waited". If TikTok changed its markup, the app
/// would happily report <em>All messages sent successfully</em> while nothing was actually delivered,
/// and the user would only find out once the streak had already broken.
/// </para>
/// <para>
/// The signal used here is the message composer: once TikTok accepts a message the composer is
/// cleared. An empty composer after sending is treated as confirmation; anything else is reported as
/// unverified rather than silently upgraded to success.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class SendVerification
{
    /// <summary>
    /// <paramref name="composerCleared"/> is null when the check could not be performed at all (for
    /// example the composer element was gone by the time we looked).
    /// </summary>
    public static SendOutcome Classify(bool scriptReportedSuccess, bool? composerCleared)
    {
        if (!scriptReportedSuccess)
            return SendOutcome.Failed;

        return composerCleared == true ? SendOutcome.Confirmed : SendOutcome.Unverified;
    }

    /// <summary>Short label for logs and history rows.</summary>
    public static string Describe(SendOutcome outcome) => outcome switch
    {
        SendOutcome.Confirmed => "confirmed",
        SendOutcome.Unverified => "unverified",
        _ => "failed",
    };

    /// <summary>
    /// Maps an outcome onto the nullable flag stored with a friend result: true for confirmed, false
    /// for unverified, null when nothing could be checked (legacy entries and errors).
    /// </summary>
    public static bool? ToVerifiedFlag(SendOutcome outcome) => outcome switch
    {
        SendOutcome.Confirmed => true,
        SendOutcome.Unverified => false,
        _ => null,
    };

    /// <summary>
    /// True when a run's results are good enough to claim everything was delivered. Unverified sends
    /// do not count, which is what stops the app from over-reporting.
    /// </summary>
    public static bool AllConfirmed(IEnumerable<bool?> verifiedFlags)
    {
        var any = false;

        foreach (var flag in verifiedFlags)
        {
            any = true;

            if (flag != true)
                return false;
        }

        return any;
    }
}
