using TiktokStreakSaver.Models;

namespace TiktokStreakSaver.Services;

public static class IosShortcutTutorialCatalog
{
    public const string MediaFolder = "ios_shortcuts_tutorial";

    /// <summary>
    /// The guide steps in the language the app is in right now.
    /// <para>
    /// Built on each read rather than once, because the text is looked up when the list is built: a field
    /// initialiser would freeze the language of the first visit and leave the guide in the old language
    /// after the switch is flipped.
    /// </para>
    /// </summary>
    public static IReadOnlyList<IosShortcutTutorialStep> Steps =>
    [
        new()
        {
            StepNumber = 1,
            TabTitle = Localization.T("Ios_Step1Tab"),
            Title = Localization.T("Ios_Step1Title"),
            Description = Localization.T("Ios_Step1Body"),
            MediaFileName = "step_01.gif"
        },
        new()
        {
            StepNumber = 2,
            TabTitle = Localization.T("Ios_Step2Tab"),
            Title = Localization.T("Ios_Step2Title"),
            Description = Localization.T("Ios_Step2Body"),
            MediaFileName = "step_02.gif"
        },
        new()
        {
            StepNumber = 3,
            TabTitle = Localization.T("Ios_Step3Tab"),
            Title = Localization.T("Ios_Step3Title"),
            Description = Localization.T("Ios_Step3Body"),
            MediaFileName = "step_03.gif"
        },
        new()
        {
            StepNumber = 4,
            TabTitle = Localization.T("Ios_Step4Tab"),
            Title = Localization.T("Ios_Step4Title"),
            Description = Localization.T("Ios_Step4Body"),
            MediaFileName = "step_04.gif"
        },
        new()
        {
            StepNumber = 5,
            TabTitle = Localization.T("Ios_Step5Tab"),
            Title = Localization.T("Ios_Step5Title"),
            Description = Localization.T("Ios_Step5Body"),
            MediaFileName = "step_05.gif"
        },
        new()
        {
            StepNumber = 6,
            TabTitle = Localization.T("Ios_Step6Tab"),
            Title = Localization.T("Ios_Step6Title"),
            Description = Localization.T("Ios_Step6Body"),
            MediaFileName = "step_06.gif"
        },
        new()
        {
            StepNumber = 7,
            TabTitle = Localization.T("Ios_Step7Tab"),
            Title = Localization.T("Ios_Step7Title"),
            Description = Localization.T("Ios_Step7Body"),
            MediaFileName = "step_06.gif"
        }
    ];

    public static string MediaAssetPath(string fileName)
        => $"{MediaFolder}/{fileName}";
}
