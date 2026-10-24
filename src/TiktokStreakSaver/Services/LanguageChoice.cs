namespace TiktokStreakSaver.Services;

/// <summary>
/// The decisions behind the language setting, with nothing around them.
/// <para>
/// Which language a choice means, and how a choice is written down, are pure functions - so they live here
/// rather than on <see cref="Localization"/>, which owns MAUI storage and cannot be compiled into the
/// headless test project. <see cref="Localization"/> is the only caller in the app.
/// </para>
/// </summary>
public static class LanguageChoice
{
    /// <summary>
    /// The language a choice means, given what the phone reports. Follow-the-phone resolves to the phone's
    /// language; the two fixed choices ignore it.
    /// </summary>
    public static AppLanguage Resolve(AppLanguageChoice choice, AppLanguage systemLanguage) => choice switch
    {
        AppLanguageChoice.English => AppLanguage.English,
        AppLanguageChoice.Indonesian => AppLanguage.Indonesian,
        _ => systemLanguage,
    };

    /// <summary>
    /// The stored form. Kept short and stable, because it is written to disk.
    /// </summary>
    public static string Serialize(AppLanguageChoice choice) => choice switch
    {
        AppLanguageChoice.English => "en",
        AppLanguageChoice.Indonesian => "id",
        _ => "system",
    };

    /// <summary>
    /// The choice a stored value means. Anything unrecognised - an empty setting, or a value written by a
    /// build that knew a language this one does not - reads as following the phone, which is the default.
    /// <para>
    /// This is what makes an upgrade from a build without this option adopt the phone's language: the old
    /// build stored "en" or "id" only when the user had chosen explicitly, and an untouched install has an
    /// empty setting, which now means "follow the phone".
    /// </para>
    /// </summary>
    public static AppLanguageChoice Parse(string? stored)
    {
        var value = stored?.Trim();

        if (string.Equals(value, "en", StringComparison.OrdinalIgnoreCase))
            return AppLanguageChoice.English;

        if (string.Equals(value, "id", StringComparison.OrdinalIgnoreCase))
            return AppLanguageChoice.Indonesian;

        return AppLanguageChoice.System;
    }
}
