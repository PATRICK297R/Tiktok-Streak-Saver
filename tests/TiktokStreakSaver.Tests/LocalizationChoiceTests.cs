using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// Checks the "follow the phone" default and the readable form of a stored failure reason.
/// <para>
/// Both are pure decisions - a culture name in, a language out; a stored sentence in, a readable one out -
/// which is why they live in MAUI-free classes and can be pinned here rather than only tried by hand on a
/// phone set to a particular language.
/// </para>
/// </summary>
public class LocalizationChoiceTests
{
    /// <summary>
    /// The phone's own language decides the interface, in every spelling Android reports it in.
    /// </summary>
    [Theory]
    [InlineData("id")]
    [InlineData("id-ID")]
    [InlineData("id_ID")]
    [InlineData("ID-id")]
    [InlineData("in")]
    [InlineData("in-ID")]
    public void AnIndonesianPhoneReadsAsIndonesian(string cultureName)
        => Assert.Equal(AppLanguage.Indonesian, AppStrings.FromCultureName(cultureName));

    /// <summary>
    /// Anything else is English, including the empty name a stripped-down runtime reports.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("ms-MY")]
    [InlineData("jv-ID")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EverythingElseReadsAsEnglish(string? cultureName)
        => Assert.Equal(AppLanguage.English, AppStrings.FromCultureName(cultureName));

    /// <summary>
    /// Following the phone defers to what the phone says; a fixed choice ignores it. This is the whole
    /// distinction the picker offers, so it is worth pinning directly.
    /// </summary>
    [Theory]
    [InlineData(AppLanguageChoice.System, AppLanguage.Indonesian, AppLanguage.Indonesian)]
    [InlineData(AppLanguageChoice.System, AppLanguage.English, AppLanguage.English)]
    [InlineData(AppLanguageChoice.English, AppLanguage.Indonesian, AppLanguage.English)]
    [InlineData(AppLanguageChoice.English, AppLanguage.English, AppLanguage.English)]
    [InlineData(AppLanguageChoice.Indonesian, AppLanguage.English, AppLanguage.Indonesian)]
    [InlineData(AppLanguageChoice.Indonesian, AppLanguage.Indonesian, AppLanguage.Indonesian)]
    public void FollowingThePhoneDefersToItAndAFixedChoiceDoesNot(
        AppLanguageChoice choice, AppLanguage phoneLanguage, AppLanguage expected)
        => Assert.Equal(expected, LanguageChoice.Resolve(choice, phoneLanguage));

    /// <summary>
    /// A setting written by a build that had no "follow the phone" option reads as that option now, which
    /// is what makes an upgrade from the previous release adopt the phone's language without a migration.
    /// </summary>
    [Theory]
    [InlineData("en", AppLanguageChoice.English)]
    [InlineData("id", AppLanguageChoice.Indonesian)]
    [InlineData("system", AppLanguageChoice.System)]
    [InlineData("", AppLanguageChoice.System)]
    [InlineData("   ", AppLanguageChoice.System)]
    [InlineData("klingon", AppLanguageChoice.System)]
    [InlineData(null, AppLanguageChoice.System)]
    public void AStoredSettingRoundTripsOrFallsBackToFollowingThePhone(
        string? stored, AppLanguageChoice expected)
        => Assert.Equal(expected, LanguageChoice.Parse(stored));

    /// <summary>Every choice survives being written and read back.</summary>
    [Theory]
    [InlineData(AppLanguageChoice.System)]
    [InlineData(AppLanguageChoice.English)]
    [InlineData(AppLanguageChoice.Indonesian)]
    public void EveryChoiceRoundTrips(AppLanguageChoice choice)
        => Assert.Equal(choice, LanguageChoice.Parse(LanguageChoice.Serialize(choice)));

    /// <summary>
    /// The reasons the automation script reports are English sentences, and they used to reach the history
    /// list untranslated. Each one now resolves to a key in the table.
    /// </summary>
    [Theory]
    [InlineData("Automation timed out after 60s", "Hist_ErrorTimeout")]
    [InlineData("Watchdog timeout after 75s", "Hist_ErrorTimeout")]
    [InlineData("User not found in chat list", "Hist_ErrorUserNotFound")]
    [InlineData("Could not type into the message composer", "Hist_ErrorComposer")]
    [InlineData("Message input not found", "Hist_ErrorComposer")]
    [InlineData("Failed to type message in composer", "Hist_ErrorComposer")]
    [InlineData("Chat panel failed after send", "Hist_ErrorChatPanel")]
    [InlineData("Chat panel is broken - reload the messages page", "Hist_ErrorChatPanel")]
    [InlineData("No chat items found after 30s wait", "Hist_ErrorNoChats")]
    [InlineData("Exception in chat traversal: something broke", "Hist_ErrorScript")]
    public void AScriptFailureReasonResolvesToItsTranslation(string stored, string expectedKey)
    {
        // Pinned in English: the table's English column is the source text, so this proves the mapping
        // without depending on whichever language the test run happens to leave behind.
        AppStrings.SetLanguage(AppLanguage.English);

        Assert.Equal(AppStrings.English(expectedKey), RunFailureReason.Describe(stored));
    }

    /// <summary>
    /// A reason nobody has taught the app about is passed through rather than replaced by a vague
    /// placeholder - the detail is what tells the user what to do.
    /// </summary>
    [Fact]
    public void AnUnknownReasonIsPassedThroughUnchanged()
    {
        const string stored = "Something the app has never seen before";

        Assert.Equal(stored, RunFailureReason.Describe(stored));
    }

    /// <summary>Nothing stored reads as nothing shown, rather than a marker.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyReasonDescribesAsEmpty(string? stored)
        => Assert.Equal(string.Empty, RunFailureReason.Describe(stored));

    /// <summary>
    /// The per-chat reason the app writes itself is already in the interface language, and has to survive
    /// being run through the translator so a real failure is not blanked out.
    /// </summary>
    [Fact]
    public void TheAppsOwnNotReachedReasonStaysReadable()
    {
        AppStrings.SetLanguage(AppLanguage.English);

        Assert.Equal(
            AppStrings.English("Android_NotReached"),
            RunFailureReason.Describe("Run ended before this chat was reached"));
    }
}
