using System.Text.RegularExpressions;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// Checks the string table itself, and then that every key the interface asks for is in it.
/// <para>
/// A missing key is not a crash: <see cref="AppStrings.Get"/> renders <c>?Key</c>, so a page whose key
/// name has a typo draws a marker instead of the text. That is deliberate - a mistake should be visible
/// in the interface rather than silently drawing nothing - but it is only useful if something fails
/// before the build ships. These tests are that something. They read the XAML and the code-behind as
/// text, collect every key the interface names, and prove each one is in the table in both languages.
/// </para>
/// </summary>
public class AppStringsTests
{
    /// <summary>
    /// The keys whose two languages are the same on purpose: names, and words that are not translated.
    /// Any other key that matches in both columns is a translation that was never written, which is the
    /// one mistake this table cannot show on its own.
    /// </summary>
    private static readonly HashSet<string> SameInBothLanguages = new(StringComparer.Ordinal)
    {
        "App_Name",
        "Common_OK",
        "Fr_Total",
        "Prof_Data",
        "Prof_LanguageEnglish",
        "Ios_WkWebView",
        "Ios_AppIntentsOpenApp",
        "Ios_Step6Tab",
        "Android_NotificationTitle",
    };

    [Fact]
    public void EveryKeyCarriesBothLanguages()
    {
        var blank = AppStrings.Keys
            .Where(key => string.IsNullOrWhiteSpace(AppStrings.English(key))
                || string.IsNullOrWhiteSpace(AppStrings.Indonesian(key)))
            .OrderBy(key => key)
            .ToList();

        Assert.True(blank.Count == 0,
            "Keys with a blank language: " + string.Join(", ", blank));
    }

    [Fact]
    public void EveryKeyIsTranslatedUnlessTheWordIsTheSameInBothLanguages()
    {
        var untranslated = AppStrings.Keys
            .Where(key => !SameInBothLanguages.Contains(key))
            .Where(key => string.Equals(AppStrings.English(key), AppStrings.Indonesian(key), StringComparison.Ordinal))
            .OrderBy(key => key)
            .ToList();

        Assert.True(untranslated.Count == 0,
            "Keys whose Indonesian text is still the English text (translate them, or list them in "
            + nameof(SameInBothLanguages) + " if the word really is the same in both languages): "
            + string.Join(", ", untranslated));
    }

    /// <summary>
    /// A key declared twice is not an error the compiler can see - the second declaration quietly wins -
    /// so the table is read as text and the declarations are counted.
    /// </summary>
    [Fact]
    public void EveryKeyIsDeclaredOnce()
    {
        var source = ReadSource("src", "TiktokStreakSaver", "Services", "AppStrings.cs");
        if (source is null)
            return;

        var declared = Regex.Matches(source, "\\[\"(?<key>[A-Za-z0-9_]+)\"\\]\\s*=")
            .Select(match => match.Groups["key"].Value)
            .ToList();

        var declaredTwice = declared
            .GroupBy(key => key)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ({group.Count()}x)")
            .OrderBy(text => text)
            .ToList();

        Assert.True(declaredTwice.Count == 0,
            "Keys declared more than once in AppStrings.cs: " + string.Join(", ", declaredTwice));
    }

    /// <summary>Every key the interface names, in the XAML and in the code-behind, exists in the table.</summary>
    [Fact]
    public void EveryKeyTheInterfaceAsksForIsInTheTable()
    {
        var root = FindRepositoryRoot();
        if (root is null)
            return;

        var asked = new List<(string File, string Key)>();

        foreach (var file in SourceFiles(root, "*.xaml"))
        {
            var text = System.IO.File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"svc:Tr\s+(?:Key\s*=\s*)?(?<key>[A-Za-z_][A-Za-z0-9_]*)"))
                asked.Add((Relative(root, file), match.Groups["key"].Value));
        }

        foreach (var file in SourceFiles(root, "*.cs"))
        {
            var text = System.IO.File.ReadAllText(file);

            // The whole argument list is read, not just a literal right after the bracket, because a key is
            // often picked between two spellings on the spot - T(count == 1 ? "A" : "B"). Every quoted word
            // inside the call is a key, so a typo in either branch still has to be in the table.
            foreach (Match call in Regex.Matches(text, @"(?:Localization|AppStrings)\.(?:T|Get|Has)\((?<args>[^)]*)\)"))
            {
                foreach (Match match in Regex.Matches(call.Groups["args"].Value, @"""(?<key>[A-Za-z0-9_]+)"""))
                    asked.Add((Relative(root, file), match.Groups["key"].Value));
            }
        }

        var missing = asked
            .Where(entry => !AppStrings.Has(entry.Key))
            .Select(entry => $"{entry.Key} (asked for in {entry.File})")
            .Distinct()
            .OrderBy(text => text)
            .ToList();

        Assert.True(missing.Count == 0,
            "Keys the interface asks for but the table does not have: " + string.Join("; ", missing));
    }

    private static IEnumerable<string> SourceFiles(string root, string pattern)
    {
        var separator = System.IO.Path.DirectorySeparatorChar;
        var directory = System.IO.Path.Combine(root, "src", "TiktokStreakSaver");

        return System.IO.Directory
            .EnumerateFiles(directory, pattern, System.IO.SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}bin{separator}")
                && !file.Contains($"{separator}obj{separator}"));
    }

    private static string Relative(string root, string file) =>
        System.IO.Path.GetRelativePath(root, file).Replace(System.IO.Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// The repository root, found by walking up from wherever the tests were started. Null when the source
    /// tree is not reachable, which the callers treat as nothing to check.
    /// </summary>
    private static string? FindRepositoryRoot()
    {
        var candidates = new[]
        {
            AppContext.BaseDirectory,
            System.IO.Directory.GetCurrentDirectory(),
        };

        foreach (var candidate in candidates)
        {
            var directory = new System.IO.DirectoryInfo(candidate);
            while (directory is not null)
            {
                var marker = System.IO.Path.Combine(
                    directory.FullName, "src", "TiktokStreakSaver", "TiktokStreakSaver.csproj");
                if (System.IO.File.Exists(marker))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static string? ReadSource(params string[] relativeParts) =>
        FindRepositoryRoot() is { } root
            ? System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { root }.Concat(relativeParts).ToArray()))
            : null;
}
