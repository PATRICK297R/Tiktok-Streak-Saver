namespace TiktokStreakSaver.Services;

/// <summary>
/// The one place that knows which language the interface is in, and how to look a string up in it.
/// <para>
/// English stays the default: an existing install shows exactly what it showed before, and nothing has to
/// be migrated. The choice is stored with the other settings so it survives a restart, and is read once at
/// startup rather than per lookup.
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

    private AppLanguage _language = AppLanguage.English;

    private Localization()
    {
    }

    public AppLanguage Language => _language;

    /// <summary>
    /// The string for a key, in the current language. Bound to by XAML through the <c>Tr</c> markup
    /// extension, so this is the indexer rather than a method.
    /// </summary>
    public string this[string key] => AppStrings.Get(key, _language);

    /// <summary>For code-behind: the string for a key in the current language.</summary>
    public static string T(string key) => AppStrings.T(key);

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised after the language changes, for the places that cannot bind: text built in code-behind, and
    /// anything already on screen that was set once.
    /// </summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// Reads the stored choice. Called once at startup; a failure falls back to English rather than
    /// leaving the app with no language at all.
    /// </summary>
    public void Load()
    {
        try
        {
            var stored = Storage.AppStorageProvider.Current.GetString(AppConstants.LanguageKey, string.Empty);
            _language = Parse(stored);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Localization.Load failed: {ex.Message}");
            _language = AppLanguage.English;
        }

        // Mirrored into AppStrings so the MAUI-free services can render in the same language without
        // reaching for this class.
        AppStrings.SetLanguage(_language);
    }

    /// <summary>Changes the language, stores it, and tells everything on screen to redraw.</summary>
    public void SetLanguage(AppLanguage language)
    {
        if (_language == language)
            return;

        _language = language;

        // The MAUI-free services read this one; see AppStrings.Language.
        AppStrings.SetLanguage(language);

        try
        {
            Storage.AppStorageProvider.Current.SetString(AppConstants.LanguageKey, Serialize(language));
        }
        catch (Exception ex)
        {
            // A failed write costs the choice on the next launch, not the change now.
            System.Diagnostics.Debug.WriteLine($"Localization.SetLanguage failed to persist: {ex.Message}");
        }

        // "Item[]" rather than the key name: every XAML binding goes through the indexer, and this is the
        // notification that refreshes all of them.
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The stored form. Kept short and stable, because it is written to disk.</summary>
    public static string Serialize(AppLanguage language) =>
        language == AppLanguage.Indonesian ? "id" : "en";

    /// <summary>
    /// The language a stored value means. Anything unrecognised - an empty setting, or a value written by a
    /// build that knew a language this one does not - reads as English, which is the default.
    /// </summary>
    public static AppLanguage Parse(string? stored) =>
        string.Equals(stored?.Trim(), "id", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Indonesian
            : AppLanguage.English;
}
