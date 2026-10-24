namespace TiktokStreakSaver.Services;

/// <summary>
/// The one place that knows which language the interface is in, and how to look a string up in it.
/// <para>
/// What the user picks is an <see cref="AppLanguageChoice"/> - follow the phone, English, or Indonesian -
/// and what the interface uses is an <see cref="AppLanguage"/>. Following the phone is the default, so an
/// Indonesian phone opens in Indonesian with nothing to configure; the two fixed choices are there for
/// anyone who wants the interface in a language the phone is not set to.
/// </para>
/// <para>
/// The choice is stored with the other settings so it survives a restart, and is read once at startup
/// rather than per lookup.
/// </para>
/// <para>
/// Implements <see cref="System.ComponentModel.INotifyPropertyChanged"/> so XAML can bind to the indexer
/// (<c>Text="{svc:Tr Dash_Subtitle}"</c>). Raising <c>Item[]</c> is what tells every binding that the
/// whole indexer changed - the only notification that reaches all of them at once - so flipping the switch
/// redraws the visible screen without rebuilding it.
/// </para>
/// <para>
/// The lookup itself lives in <see cref="AppStrings"/>, which is free of MAUI so the table can be checked
/// by the tests.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public sealed class Localization : System.ComponentModel.INotifyPropertyChanged
{
    private static Localization? _current;

    /// <summary>The single instance every screen reads from.</summary>
    public static Localization Current => _current ??= new Localization();

    private AppLanguageChoice _choice = AppLanguageChoice.System;

    /// <summary>
    /// The language the phone reports. Only read when <see cref="Choice"/> is
    /// <see cref="AppLanguageChoice.System"/>, and refreshed by <see cref="RefreshFromSystem"/>.
    /// </summary>
    private AppLanguage _systemLanguage = AppLanguage.English;

    private Localization()
    {
    }

    /// <summary>What the user picked in Profile.</summary>
    public AppLanguageChoice Choice => _choice;

    /// <summary>The language actually in use, after the phone has been consulted if it was asked to.</summary>
    public AppLanguage Language => Resolve(_choice, _systemLanguage);

    /// <summary>
    /// The string for a key, in the current language. Bound to by XAML through the <c>Tr</c> markup
    /// extension, so this is the indexer rather than a method.
    /// </summary>
    public string this[string key] => AppStrings.Get(key, Language);

    /// <summary>For code-behind: the string for a key in the current language.</summary>
    public static string T(string key) => AppStrings.T(key);

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised after the language changes, for the places that cannot bind: text built in code-behind, and
    /// anything already on screen that was set once.
    /// </summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// The language a choice means, given what the phone reports. Follow-the-phone resolves to the phone's
    /// language; the two fixed choices ignore it.
    /// </summary>
    public static AppLanguage Resolve(AppLanguageChoice choice, AppLanguage systemLanguage) =>
        LanguageChoice.Resolve(choice, systemLanguage);

    /// <summary>
    /// Reads the stored choice and the phone's language. Called once at startup, before any window is
    /// built; a failure falls back to following the phone rather than leaving the app with no language
    /// at all.
    /// </summary>
    public void Load()
    {
        try
        {
            var stored = Storage.AppStorageProvider.Current.GetString(AppConstants.LanguageKey, string.Empty);
            _choice = Parse(stored);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Localization.Load failed: {ex.Message}");
            _choice = AppLanguageChoice.System;
        }

        _systemLanguage = DetectSystemLanguage();

        // Mirrored into AppStrings so the MAUI-free services can render in the same language without
        // reaching for this class.
        AppStrings.SetLanguage(Language);
    }

    /// <summary>
    /// Re-reads the phone's language, and tells everything on screen to redraw if that changed the
    /// language in use.
    /// <para>
    /// Android recreates the activity when the phone's language changes, but the process survives, so
    /// without this an app left running in the background would keep drawing the previous language until
    /// it was killed.
    /// </para>
    /// </summary>
    public void RefreshFromSystem()
    {
        var detected = DetectSystemLanguage();
        if (detected == _systemLanguage)
            return;

        _systemLanguage = detected;

        // Only matters while following the phone; a fixed choice is unaffected by the phone changing.
        if (_choice != AppLanguageChoice.System)
            return;

        AppStrings.SetLanguage(Language);
        NotifyChanged();
    }

    /// <summary>Changes the choice, stores it, and tells everything on screen to redraw.</summary>
    public void SetChoice(AppLanguageChoice choice)
    {
        if (_choice == choice)
            return;

        _choice = choice;

        // The phone may have changed since the last time it was read, and switching back to
        // follow-the-phone should honour what it says now rather than what it said at startup.
        if (choice == AppLanguageChoice.System)
            _systemLanguage = DetectSystemLanguage();

        // The MAUI-free services read this one; see AppStrings.Language.
        AppStrings.SetLanguage(Language);

        try
        {
            Storage.AppStorageProvider.Current.SetString(AppConstants.LanguageKey, Serialize(choice));
        }
        catch (Exception ex)
        {
            // A failed write costs the choice on the next launch, not the change now.
            System.Diagnostics.Debug.WriteLine($"Localization.SetChoice failed to persist: {ex.Message}");
        }

        NotifyChanged();
    }

    /// <summary>
    /// The phone's language. Read from the UI culture, which Android sets from the system locale.
    /// </summary>
    private static AppLanguage DetectSystemLanguage()
    {
        try
        {
            return AppStrings.FromCultureName(System.Globalization.CultureInfo.CurrentUICulture?.Name);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Localization.DetectSystemLanguage failed: {ex.Message}");
            return AppLanguage.English;
        }
    }

    /// <summary>
    /// Tells every binding and every listener that the language changed. "Item[]" rather than a key name:
    /// every XAML binding goes through the indexer, and this is the notification that refreshes all of
    /// them.
    /// </summary>
    private void NotifyChanged()
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The stored form. See <see cref="LanguageChoice.Serialize"/>.</summary>
    public static string Serialize(AppLanguageChoice choice) => LanguageChoice.Serialize(choice);

    /// <summary>The choice a stored value means. See <see cref="LanguageChoice.Parse"/>.</summary>
    public static AppLanguageChoice Parse(string? stored) => LanguageChoice.Parse(stored);
}
