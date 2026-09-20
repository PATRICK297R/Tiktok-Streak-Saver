namespace TiktokStreakSaver.Services;

/// <summary>
/// Expands the placeholders allowed in the streak message, so a single template can greet each
/// friend by name instead of sending everyone the exact same text.
/// <para>
/// Supported tokens, matched case-insensitively and replaced everywhere they appear:
/// <list type="bullet">
///   <item><description><c>{name}</c> - the friend's display name, falling back to their username.</description></item>
///   <item><description><c>{username}</c> - the friend's TikTok handle, falling back to the display name.</description></item>
/// </list>
/// Unknown tokens are left untouched on purpose: a message that legitimately contains braces should
/// not be silently rewritten, and an unrecognised token is more useful visible than deleted.
/// </para>
/// <para>
/// The fallbacks matter for group chats, which have no @handle. Without them a template like
/// "hey {username}" would send "hey " to a group.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class MessageTemplate
{
    public const string NameToken = "{name}";
    public const string UsernameToken = "{username}";

    /// <summary>Human-readable token list for UI hints.</summary>
    public const string TokenHint = NameToken + " and " + UsernameToken;

    /// <summary>
    /// Replaces <c>{name}</c> and <c>{username}</c> in <paramref name="template"/>.
    /// Returns the template unchanged when it contains no braces.
    /// </summary>
    public static string Render(string? template, string? username, string? displayName)
    {
        if (string.IsNullOrEmpty(template))
            return template ?? string.Empty;

        // Nothing to substitute - avoids two scans and two allocations on the common path.
        if (template.IndexOf('{') < 0)
            return template;

        var usernameValue = !string.IsNullOrWhiteSpace(username) ? username! : displayName ?? string.Empty;
        var nameValue = !string.IsNullOrWhiteSpace(displayName) ? displayName! : usernameValue;

        var rendered = template.Replace(UsernameToken, usernameValue, StringComparison.OrdinalIgnoreCase);

        return rendered.Replace(NameToken, nameValue, StringComparison.OrdinalIgnoreCase);
    }
}
