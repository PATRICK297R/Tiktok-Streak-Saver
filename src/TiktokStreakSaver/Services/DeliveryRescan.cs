using TiktokStreakSaver.Models;

namespace TiktokStreakSaver.Services;

/// <summary>
/// The rules for the delivery rescan: which chats to re-check, which to resend to, and how a resend is
/// recorded.
/// <para>
/// Kept here rather than in the Android service because these are decisions, not plumbing - and the
/// decision that matters most is the one about when <em>not</em> to resend. Getting it wrong messages
/// people twice, which is worse than not verifying at all, so it is worth being able to test directly.
/// </para>
/// </summary>
public static class DeliveryRescan
{
    /// <summary>
    /// The chats worth re-checking: the ones the app believes it delivered to. A failed send is already
    /// reported as failed, and a rehearsal sent nothing.
    /// </summary>
    public static List<FriendMessageResult> Candidates(
        IEnumerable<FriendMessageResult>? results, ISet<string>? alreadyResent)
    {
        if (results is null)
            return new List<FriendMessageResult>();

        return results
            .Where(r => r != null && r.Success && !r.DryRun)
            .Where(r => alreadyResent is null || !alreadyResent.Contains(r.FriendId))
            .ToList();
    }

    /// <summary>
    /// The chats to send to again: those whose message was looked for and not found.
    /// <para>
    /// Only an explicit <c>false</c> qualifies. <c>null</c> means the chat could not be read - the selectors
    /// did not match, or the panel was broken - and resending on that would put a second message in front
    /// of someone who may already have received the first.
    /// </para>
    /// </summary>
    public static List<FriendMessageResult> NeedingResend(
        IEnumerable<FriendMessageResult>? results, ISet<string>? alreadyResent)
    {
        if (results is null)
            return new List<FriendMessageResult>();

        return results
            .Where(r => r != null && r.Success && !r.DryRun && r.Verified == false)
            .Where(r => alreadyResent is null || !alreadyResent.Contains(r.FriendId))
            .ToList();
    }

    /// <summary>
    /// Folds a resend's outcome into the existing row for that chat.
    /// <para>
    /// A resend is not a second event: it is the same chat's result being corrected. Appending would leave
    /// two rows for one friend, and the run would read as though it messaged them twice.
    /// </para>
    /// </summary>
    public static void ApplyResend(List<FriendMessageResult> results, FriendMessageResult resend)
    {
        if (results is null || resend is null)
            return;

        var existing = results.FirstOrDefault(r => r.FriendId == resend.FriendId);
        if (existing is null)
        {
            results.Add(resend);
            return;
        }

        existing.Success = resend.Success;
        existing.ErrorMessage = resend.ErrorMessage;
        existing.Verified = resend.Verified;
        existing.BubbleBaseline = resend.BubbleBaseline;
        existing.MessageText = resend.MessageText;
        existing.Timestamp = resend.Timestamp;
    }

    /// <summary>
    /// The text to look for in the thread: what was actually sent.
    /// <para>
    /// Not recomputed from the settings, because a run that picks a random variant would then be searched
    /// for with the wrong text and every chat would come back as undelivered. Null means the send predates
    /// this record, and there is nothing honest to search for.
    /// </para>
    /// </summary>
    public static string? TextToSearch(FriendMessageResult? result)
        => string.IsNullOrEmpty(result?.MessageText) ? null : result!.MessageText;

    /// <summary>
    /// The time window a chat's message is expected in, or null when the send's time was never recorded.
    /// <para>
    /// The window opens when the send was attempted rather than at the minute the user scheduled, because a
    /// run takes time. Null means a result written by a build that did not record the time, and the rescan
    /// then falls back to the text alone and reports the weaker answer as unconfirmed.
    /// </para>
    /// </summary>
    public static (DateTime Start, DateTime End)? WindowFor(FriendMessageResult? result)
    {
        if (result?.SentAt is not { } sentAt)
            return null;

        return (sentAt, DeliveryWindow.EndOf(sentAt));
    }
}
