namespace TiktokStreakSaver.Services;

/// <summary>
/// Prepares a bundled script for injection into a WebView.
/// <para>
/// The Android WebView does not reliably evaluate a multi-line script handed to
/// <c>EvaluateJavascript</c>, which is why the sending flow has always flattened its script first.
/// The same preparation therefore has to be applied to every script the app injects; a second script
/// that skipped it would look like a broken feature rather than a missing step, and the failure is
/// silent - the call returns without the script ever running.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class ScriptPreparation
{
    /// <summary>
    /// Flattens a script onto one line: full-line comments are dropped, then runs of whitespace are
    /// collapsed.
    /// <para>
    /// The order matters. Collapsing first would fold the code that follows a <c>//</c> onto the
    /// comment and delete it, so comment lines are removed while the line structure is still
    /// intact.
    /// </para>
    /// </summary>
    public static string ForInjection(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        var withoutCommentLines = string.Join("\n",
            script.Split('\n').Where(line => !line.TrimStart().StartsWith("//")));

        return System.Text.RegularExpressions.Regex
            .Replace(withoutCommentLines, @"\s+", " ")
            .Trim();
    }
}
