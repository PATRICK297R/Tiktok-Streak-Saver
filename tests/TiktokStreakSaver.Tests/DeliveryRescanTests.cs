using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// The rules the delivery rescan follows, pinned so a future change cannot quietly turn verification into
/// a send loop.
/// <para>
/// The rescan exists because "the script reported success" and "the message is in the thread" are
/// different claims. The danger in adding it is the opposite failure: if the check is too eager, a wrong
/// selector or a misread timestamp makes every chat look undelivered and the app resends to everyone -
/// worse than not verifying at all, because the user gets duplicate messages.
/// </para>
/// </summary>
public class DeliveryRescanTests
{
    private static FriendMessageResult Delivered(string id, bool? verified = null, DateTime? sentAt = null)
        => new()
        {
            FriendId = id,
            Username = id,
            Success = true,
            Verified = verified,
            MessageText = "Hey! Keeping our streak alive!",
            SentAt = sentAt ?? new DateTime(2026, 10, 26, 3, 45, 0),
        };

    private static FriendMessageResult Failed(string id)
        => new() { FriendId = id, Username = id, Success = false, ErrorMessage = "boom" };

    private static FriendMessageResult Rehearsal(string id)
        => new() { FriendId = id, Username = id, Success = true, DryRun = true };

    /// <summary>
    /// A send is only worth re-checking when the app believes it delivered something. A failed send is
    /// already reported as failed, and a rehearsal sent nothing at all.
    /// </summary>
    [Fact]
    public void OnlyDeliveredChatsAreRechecked()
    {
        var results = new List<FriendMessageResult>
        {
            Delivered("a"),
            Failed("b"),
            Rehearsal("c"),
        };

        var candidates = DeliveryRescan.Candidates(results, alreadyResent: null);

        Assert.Equal(new[] { "a" }, candidates.Select(r => r.FriendId).ToArray());
    }

    /// <summary>
    /// A chat whose message was not found is resent. This is the whole point of the pass.
    /// </summary>
    [Fact]
    public void AMessageNotFoundIsResent()
    {
        var results = new List<FriendMessageResult>
        {
            Delivered("a", verified: false),
            Delivered("b", verified: true),
        };

        var missing = DeliveryRescan.NeedingResend(results, alreadyResent: null);

        Assert.Equal(new[] { "a" }, missing.Select(r => r.FriendId).ToArray());
    }

    /// <summary>
    /// A chat that could not be read is left alone.
    /// <para>
    /// "Unknown" means the times could not be read, or the markup was not recognised. Resending on that
    /// would send a second message to someone who may have received the first - the one outcome the app
    /// must never produce. A null <see cref="FriendMessageResult.Verified"/> is exactly this state.
    /// </para>
    /// </summary>
    [Fact]
    public void AnUnreadableChatIsNeverResent()
    {
        var results = new List<FriendMessageResult>
        {
            Delivered("a", verified: null),
        };

        Assert.Empty(DeliveryRescan.NeedingResend(results, alreadyResent: null));
    }

    /// <summary>
    /// A chat is resent at most once. Without this, a disagreement with TikTok's markup would resend on
    /// every pass and the user would be messaged repeatedly.
    /// </summary>
    [Fact]
    public void AChatIsResentOnlyOnce()
    {
        var results = new List<FriendMessageResult>
        {
            Delivered("a", verified: false),
        };

        var alreadyResent = new HashSet<string>(StringComparer.Ordinal) { "a" };

        Assert.Empty(DeliveryRescan.NeedingResend(results, alreadyResent));
    }

    /// <summary>
    /// A resend must not add a second row for the same chat: the history would then show one friend twice
    /// and the run would look like it messaged them twice.
    /// </summary>
    [Fact]
    public void AResendReplacesTheExistingRowRatherThanAppending()
    {
        var results = new List<FriendMessageResult> { Delivered("a", verified: false) };

        DeliveryRescan.ApplyResend(results, new FriendMessageResult
        {
            FriendId = "a",
            Username = "a",
            Success = true,
            Verified = true,
            MessageText = "Hey! Keeping our streak alive!",
            SentAt = new DateTime(2026, 10, 26, 3, 50, 0),
        });

        Assert.Single(results);
        Assert.True(results[0].Verified);
    }

    /// <summary>A resend that failed again is recorded on the same row, not added as a second one.</summary>
    [Fact]
    public void AFailedResendUpdatesTheRowInPlace()
    {
        var results = new List<FriendMessageResult> { Delivered("a", verified: false) };

        DeliveryRescan.ApplyResend(results, new FriendMessageResult
        {
            FriendId = "a",
            Username = "a",
            Success = false,
            ErrorMessage = "Could not type into the message composer",
        });

        Assert.Single(results);
        Assert.False(results[0].Success);
        Assert.Equal("Could not type into the message composer", results[0].ErrorMessage);
    }

    /// <summary>
    /// The message searched for is the one that was actually sent, not the one that would be sent now.
    /// <para>
    /// A run that picks a random variant from a pool would otherwise have the rescan searching for text
    /// that never went out, and every chat would look undelivered.
    /// </para>
    /// </summary>
    [Fact]
    public void TheRescanSearchesForTheTextThatWasSent()
    {
        var result = Delivered("a");
        result.MessageText = "Variant three";

        Assert.Equal("Variant three", DeliveryRescan.TextToSearch(result));
    }

    /// <summary>
    /// A result recorded by a build that did not store the text cannot be verified, and must say so rather
    /// than guess at a message.
    /// </summary>
    [Fact]
    public void WithoutARecordedTextThereIsNothingToSearchFor()
    {
        var result = Delivered("a");
        result.MessageText = null;

        Assert.Null(DeliveryRescan.TextToSearch(result));
    }
}

/// <summary>
/// The time window the rescan looks in.
/// <para>
/// This is the part that replaced counting bubbles: the streak message is the same every day, so the count
/// could not tell yesterday's copy from today's. A timestamp can.
/// </para>
/// </summary>
public class DeliveryWindowTests
{
    private static readonly DateTime SentAt = new(2026, 10, 26, 3, 45, 0);

    /// <summary>
    /// The window opens at the attempt, not at the scheduled minute. A run takes time, and a message that
    /// lands a few minutes after 03:45 is still the message the run sent.
    /// </summary>
    [Fact]
    public void AMessageSentLateIsStillInsideTheWindow()
    {
        Assert.True(DeliveryWindow.Covers(SentAt, SentAt.AddMinutes(7)));
    }

    /// <summary>
    /// A message stamped exactly at the attempt counts - the automation injects and the bubble appears at
    /// or after that moment, never before.
    /// </summary>
    [Fact]
    public void TheAttemptItselfCounts()
    {
        Assert.True(DeliveryWindow.Covers(SentAt, SentAt));
    }

    /// <summary>
    /// The end is inclusive, so a message landing exactly on the boundary is not thrown away.
    /// </summary>
    [Fact]
    public void TheEndOfTheWindowIsInclusive()
    {
        Assert.True(DeliveryWindow.Covers(SentAt, DeliveryWindow.EndOf(SentAt)));
    }

    /// <summary>
    /// Anything after the window is a different message - the next run's, or the user's own - and must not
    /// be mistaken for this one.
    /// </summary>
    [Fact]
    public void AMessageAfterTheWindowDoesNotCount()
    {
        Assert.False(DeliveryWindow.Covers(SentAt, SentAt.AddMinutes(DeliveryWindow.WindowMinutes + 1)));
    }

    /// <summary>
    /// Yesterday's identical message is before the window, which is exactly the case counting bubbles could
    /// not handle.
    /// </summary>
    [Fact]
    public void YesterdaysIdenticalMessageDoesNotCount()
    {
        Assert.False(DeliveryWindow.Covers(SentAt, SentAt.AddDays(-1)));
    }

    /// <summary>
    /// A message stamped a little before the attempt is not covered. This is the one judgement call here:
    /// a separator carries the time of the first message in its group, so a message can appear slightly
    /// earlier than it was really sent - and the script widens the window at the start rather than the end
    /// to absorb that.
    /// </summary>
    [Fact]
    public void TheWindowIsFifteenMinutesLong()
    {
        Assert.Equal(15, DeliveryWindow.WindowMinutes);
        Assert.Equal(SentAt.AddMinutes(15), DeliveryWindow.EndOf(SentAt));
    }
}
