namespace TiktokStreakSaver.Services;

/// <summary>
/// Chooses which text to send to a given friend.
/// <para>
/// Precedence, highest first:
/// <list type="number">
///   <item><description>This friend's own message (<c>FriendConfig.CustomMessage</c>).</description></item>
///   <item><description>A message from the randomized pool, when that option is on.</description></item>
///   <item><description>The shared message from the Dashboard.</description></item>
/// </list>
/// A per-friend message always wins, because it is the most specific thing the user typed - an
/// explicit choice should not be overruled by a pool of generic variants.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class MessageResolver
{
    /// <summary>
    /// Returns the text to send. <paramref name="randomizedMessage"/> should be null when the
    /// randomized option is off.
    /// </summary>
    public static string Resolve(string? customMessage, string? randomizedMessage, string? sharedMessage)
    {
        if (!string.IsNullOrWhiteSpace(customMessage))
            return customMessage!;

        if (!string.IsNullOrWhiteSpace(randomizedMessage))
            return randomizedMessage!;

        return sharedMessage ?? string.Empty;
    }

    /// <summary>True when this text overrides the shared message.</summary>
    public static bool IsOverride(string? customMessage) => !string.IsNullOrWhiteSpace(customMessage);

    /// <summary>
    /// Normalises text typed by the user for storage: trimmed, and blank becomes null so "no override"
    /// has exactly one representation.
    /// </summary>
    public static string? Normalize(string? customMessage)
    {
        if (string.IsNullOrWhiteSpace(customMessage))
            return null;

        return customMessage.Trim();
    }
}
