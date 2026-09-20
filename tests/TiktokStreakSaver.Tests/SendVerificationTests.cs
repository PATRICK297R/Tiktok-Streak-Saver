using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

public class SendVerificationTests
{
    [Fact]
    public void ConfirmedWhenTheComposerCleared()
    {
        Assert.Equal(SendOutcome.Confirmed,
            SendVerification.Classify(scriptReportedSuccess: true, composerCleared: true));
    }

    [Fact]
    public void UnverifiedWhenTheComposerStillHoldsText()
    {
        // Regression guard for the old behaviour, where "success" meant nothing more than
        // "we clicked send and waited a second".
        Assert.Equal(SendOutcome.Unverified,
            SendVerification.Classify(scriptReportedSuccess: true, composerCleared: false));
    }

    [Fact]
    public void UnverifiedWhenTheComposerCouldNotBeChecked()
    {
        Assert.Equal(SendOutcome.Unverified,
            SendVerification.Classify(scriptReportedSuccess: true, composerCleared: null));
    }

    [Fact]
    public void FailedWhenTheScriptReportedAnErrorWhateverTheComposerDid()
    {
        Assert.Equal(SendOutcome.Failed, SendVerification.Classify(false, true));
        Assert.Equal(SendOutcome.Failed, SendVerification.Classify(false, false));
        Assert.Equal(SendOutcome.Failed, SendVerification.Classify(false, null));
    }

    [Fact]
    public void VerifiedFlagMapsOnlyConfirmationToTrue()
    {
        Assert.True(SendVerification.ToVerifiedFlag(SendOutcome.Confirmed));
        Assert.False(SendVerification.ToVerifiedFlag(SendOutcome.Unverified));
        Assert.Null(SendVerification.ToVerifiedFlag(SendOutcome.Failed));
    }

    [Fact]
    public void DescribesEachOutcome()
    {
        Assert.Equal("confirmed", SendVerification.Describe(SendOutcome.Confirmed));
        Assert.Equal("unverified", SendVerification.Describe(SendOutcome.Unverified));
        Assert.Equal("failed", SendVerification.Describe(SendOutcome.Failed));
    }

    [Fact]
    public void AllConfirmedRequiresAtLeastOneAndNoGaps()
    {
        Assert.True(SendVerification.AllConfirmed(new bool?[] { true, true }));

        // An unverified or unchecked entry means we cannot claim delivery.
        Assert.False(SendVerification.AllConfirmed(new bool?[] { true, false }));
        Assert.False(SendVerification.AllConfirmed(new bool?[] { true, null }));

        // Nothing attempted is not a success either.
        Assert.False(SendVerification.AllConfirmed(Array.Empty<bool?>()));
    }
}

public class SendConfirmationSummaryTests
{
    private static FriendMessageResult Result(bool success, bool? verified, bool dryRun = false) =>
        new() { FriendId = Guid.NewGuid().ToString(), Username = "x", Success = success, Verified = verified, DryRun = dryRun };

    [Fact]
    public void CountsEachOutcomeSeparately()
    {
        var results = new List<FriendMessageResult>
        {
            Result(true, true),         // confirmed
            Result(true, true),         // confirmed
            Result(true, false),        // unverified
            Result(true, null),         // unverified (nothing checked it)
            Result(false, null),        // failed
        };

        var (attempted, confirmed, unverified, failed) = SendConfirmationSummary.Summarize(results);

        Assert.Equal(5, attempted);
        Assert.Equal(2, confirmed);
        Assert.Equal(2, unverified);
        Assert.Equal(1, failed);
    }

    [Fact]
    public void DryRunRowsAreIgnoredEntirely()
    {
        var results = new List<FriendMessageResult>
        {
            Result(true, true),
            Result(true, true, dryRun: true),
            Result(true, true, dryRun: true),
        };

        var (attempted, confirmed, _, _) = SendConfirmationSummary.Summarize(results);

        Assert.Equal(1, attempted);
        Assert.Equal(1, confirmed);
    }

    [Fact]
    public void AllDeliveredIsFalseWhenAnythingIsUnverified()
    {
        Assert.True(SendConfirmationSummary.AllDelivered(new List<FriendMessageResult> { Result(true, true) }));

        Assert.False(SendConfirmationSummary.AllDelivered(new List<FriendMessageResult>
        {
            Result(true, true),
            Result(true, false),
        }));

        Assert.False(SendConfirmationSummary.AllDelivered(new List<FriendMessageResult> { Result(false, null) }));
        Assert.False(SendConfirmationSummary.AllDelivered(new List<FriendMessageResult>()));
        Assert.False(SendConfirmationSummary.AllDelivered(null));
    }

    [Fact]
    public void AllDeliveredIgnoresADryRunWhenRealSendsSucceeded()
    {
        var results = new List<FriendMessageResult>
        {
            Result(true, true),
            Result(false, null, dryRun: true),
        };

        Assert.True(SendConfirmationSummary.AllDelivered(results));
    }

    [Fact]
    public void DescribeReportsTheBreakdown()
    {
        Assert.Equal("2 sent", SendConfirmationSummary.Describe(new List<FriendMessageResult>
        {
            Result(true, true), Result(true, true),
        }));

        Assert.Equal("1 sent, 1 unverified", SendConfirmationSummary.Describe(new List<FriendMessageResult>
        {
            Result(true, true), Result(true, false),
        }));

        Assert.Equal("1 sent, 1 unverified, 1 failed", SendConfirmationSummary.Describe(new List<FriendMessageResult>
        {
            Result(true, true), Result(true, null), Result(false, null),
        }));

        Assert.Equal("nothing to send", SendConfirmationSummary.Describe(new List<FriendMessageResult>()));
    }
}
