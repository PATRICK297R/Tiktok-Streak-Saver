using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// The rule that matters here is that one notification tap opens the page once. Two places act on the
/// request - the resume that follows the tap, and the shell's own first navigation - and either may get
/// there first, so the request has to be collectable exactly once.
/// </summary>
public class ContactImportLaunchTests
{
    public ContactImportLaunchTests() => ContactImportLaunch.Clear();

    [Fact]
    public void NothingIsPendingToStartWith()
        => Assert.False(ContactImportLaunch.IsPending);

    [Fact]
    public void ATapIsPendingUntilItIsCollected()
    {
        ContactImportLaunch.Request();

        Assert.True(ContactImportLaunch.IsPending);
    }

    [Fact]
    public void CollectingATapReportsIt()
    {
        ContactImportLaunch.Request();

        Assert.True(ContactImportLaunch.TryTake());
    }

    [Fact]
    public void CollectingTwiceReportsItOnce()
    {
        ContactImportLaunch.Request();

        Assert.True(ContactImportLaunch.TryTake());
        Assert.False(ContactImportLaunch.TryTake());
    }

    [Fact]
    public void CollectingWithNothingPendingReportsNothing()
        => Assert.False(ContactImportLaunch.TryTake());

    /// <summary>
    /// A second import started later must still open its page. If taking the request left the state
    /// unusable, only the first notification of a session would ever work.
    /// </summary>
    [Fact]
    public void ASecondTapIsHonoured()
    {
        ContactImportLaunch.Request();
        Assert.True(ContactImportLaunch.TryTake());

        ContactImportLaunch.Request();
        Assert.True(ContactImportLaunch.TryTake());
    }

    [Fact]
    public void ClearingForgetsAPendingTap()
    {
        ContactImportLaunch.Request();

        ContactImportLaunch.Clear();

        Assert.False(ContactImportLaunch.IsPending);
        Assert.False(ContactImportLaunch.TryTake());
    }

    /// <summary>
    /// The page can be asked to open while it is already open, and that request is dropped rather than
    /// left pending - otherwise it would open the page again at the next navigation.
    /// </summary>
    [Fact]
    public void ClearingAnAlreadyOpenRequestLeavesNothingBehind()
    {
        ContactImportLaunch.Request();
        ContactImportLaunch.Clear();

        ContactImportLaunch.Request();

        Assert.True(ContactImportLaunch.TryTake());
    }
}
