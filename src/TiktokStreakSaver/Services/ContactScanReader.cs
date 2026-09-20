namespace TiktokStreakSaver.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
using TiktokStreakSaver.Models;

/// <summary>One chat as the scanner reported it.</summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class ContactScanItemDto
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("isGroup")] public bool IsGroup { get; set; }
}

/// <summary>The scanner's running state, read back while it works.</summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class ContactScanDto
{
    [JsonPropertyName("done")] public bool Done { get; set; }
    [JsonPropertyName("items")] public List<ContactScanItemDto> Items { get; set; } = new();
    [JsonPropertyName("attempts")] public int Attempts { get; set; }

    /// <summary>
    /// The page the scanner ended up on. Reported because "found nothing" and "was looking at the
    /// wrong page" need telling apart, and the page is the only evidence of which one happened.
    /// </summary>
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;

    /// <summary>The user agent actually in force inside the page.</summary>
    [JsonPropertyName("ua")] public string UserAgent { get; set; } = string.Empty;

    /// <summary>The scanner's own progress notes, newest last.</summary>
    [JsonPropertyName("log")] public List<string> Log { get; set; } = new();
}

/// <summary>
/// Reads what the contact scanner found and turns it into entries the app can keep.
/// <para>
/// The scanner runs inside the page and reports as it goes, so everything it sends has to be treated
/// as untrusted input: it is text scraped from a DOM that TikTok can change at any time. Entries
/// that could never be sent to are dropped here rather than being stored and failing later.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class ContactScanReader
{
    /// <summary>
    /// Name the scanner calls from inside the page to report back.
    /// <para>
    /// Lives here rather than on the page so that the test suite can compare it against the bundled
    /// script. The name appears in two languages, and a mismatch fails silently - the script runs,
    /// the reports go nowhere, and the page concludes the scanner never started.
    /// </para>
    /// </summary>
    public const string BridgeName = "SaverScan";

    /// <summary>
    /// Normalises what <c>EvaluateJavaScriptAsync</c> hands back before it is parsed.
    /// <para>
    /// Android returns the platform's JSON encoding of the JavaScript value, so a script that
    /// returned a string arrives wrapped in quotes with its own quotes escaped. Unwrapping here
    /// keeps that platform quirk in one place instead of scattered through the page, and makes it
    /// something that can be tested rather than guessed at on a device.
    /// </para>
    /// </summary>
    public static string NormalizeJavaScriptResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var trimmed = raw.Trim();

        // "null" is what a script that returned nothing produces; there is nothing to parse.
        if (trimmed == "null")
            return string.Empty;

        if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string>(trimmed) ?? string.Empty;
            }
            catch (JsonException)
            {
                // Not actually a quoted JSON string after all; hand it back untouched.
                return trimmed;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Parses one poll of the scanner. Returns false only when the payload cannot be read at all,
    /// which during a scan just means "try again next tick".
    /// </summary>
    public static bool TryParse(string? json, out ContactScanDto? scan, out string? error)
    {
        scan = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "No scan data yet.";
            return false;
        }

        try
        {
            scan = JsonSerializer.Deserialize(json, AppJsonContext.Default.ContactScanDto);
        }
        catch (JsonException)
        {
            error = "The scan data could not be read.";
            return false;
        }

        if (scan is null)
        {
            error = "The scan data could not be read.";
            return false;
        }

        scan.Items ??= new List<ContactScanItemDto>();
        scan.Log ??= new List<string>();
        scan.Url ??= string.Empty;
        scan.UserAgent ??= string.Empty;
        return true;
    }

    /// <summary>
    /// Explains why a scan produced nothing, in terms the user can act on and I can debug from.
    /// <para>
    /// "No conversations were found" is true but useless: it covers an inbox that is genuinely empty,
    /// a page that never reached the inbox, and a scanner that stopped early - which are three
    /// different problems. The scanner reports where it ended up and how far it got, so the message
    /// can say which one this was.
    /// </para>
    /// </summary>
    public static string DescribeEmptyScan(ContactScanDto? scan)
    {
        if (scan is null)
            return "The reader never reached TikTok's inbox, so nothing could be read.";

        var lastStep = scan.Log.Count > 0 ? scan.Log[^1] : null;

        // A blank page is its own case and needs saying plainly. Reported as "did not stay on the inbox" it
        // reads as though TikTok had thrown the reader out, when what actually happened is that the inbox
        // never loaded at all - which is a different problem with a different fix.
        if (WebViewNavigationPolicy.IsBlank(scan.Url))
        {
            return "The reader ran on a page that was still blank, so TikTok's inbox never actually loaded. "
                 + "That is usually the connection rather than your account — check it, then try again.";
        }

        // A page that is not the inbox means the scan was looking in the wrong place entirely, which
        // is worth saying plainly rather than reporting as an empty list.
        if (scan.Url.Length > 0 && !scan.Url.Contains("/messages", StringComparison.OrdinalIgnoreCase))
        {
            return $"TikTok did not stay on the inbox — it ended up at {scan.Url}. "
                 + "If that is a sign-in page, sign in again from the Profile page and retry.";
        }

        if (scan.Attempts == 0)
            return "The reader started but never managed to inspect the page.";

        return "TikTok's inbox was opened but no conversation rows could be read from it. "
             + $"Steps taken: {scan.Attempts}."
             + (string.IsNullOrWhiteSpace(lastStep) ? string.Empty : $" Last step: {lastStep}")
             + (string.IsNullOrWhiteSpace(scan.UserAgent) ? string.Empty : $" Client: {Shorten(scan.UserAgent)}");
    }

    /// <summary>The browser name at the front of a user agent, which is the part that matters here.</summary>
    private static string Shorten(string userAgent)
    {
        var text = userAgent.Trim();
        if (text.Length <= 60)
            return text;

        return text[..60] + "…";
    }

    /// <summary>
    /// Turns scanned chats into friends and groups, dropping anything unusable.
    /// <para>
    /// A person needs a handle, because that is what the sending flow navigates by. A group is
    /// matched on its chat header, so it needs a name. Anything missing its required field is
    /// skipped rather than stored as a row that would fail every run.
    /// </para>
    /// </summary>
    public static List<FriendConfig> ToFriends(IEnumerable<ContactScanItemDto>? items)
    {
        var friends = new List<FriendConfig>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items ?? [])
        {
            if (item is null)
                continue;

            var displayName = (item.DisplayName ?? string.Empty).Replace('\n', ' ').Trim();
            var username = (item.Username ?? string.Empty).Trim().TrimStart('@').Trim();

            if (item.IsGroup)
            {
                if (string.IsNullOrWhiteSpace(displayName))
                    continue;

                if (!seen.Add("group:" + displayName))
                    continue;

                friends.Add(new FriendConfig
                {
                    Username = string.Empty,
                    DisplayName = displayName,
                    IsGroup = true,
                    IsEnabled = true
                });
            }
            else
            {
                if (string.IsNullOrWhiteSpace(username))
                    continue;

                if (!seen.Add("user:" + username))
                    continue;

                friends.Add(new FriendConfig
                {
                    Username = username,

                    // The display name is a guess read off the chat row; a person with only a handle
                    // still works perfectly, so the handle fills in.
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName,
                    IsGroup = false,
                    IsEnabled = true
                });
            }
        }

        return friends;
    }

    /// <summary>
    /// Scanned chats that are not already in the given list.
    /// <para>
    /// The "already there" test is <see cref="BackupMergePolicy"/>, the same matching a restore uses,
    /// so an imported person and a restored person cannot come to mean two different things by
    /// "the same chat". Scanned entries carry fresh ids, so matching on the handle or group name is
    /// what actually keeps a second import from adding everything twice.
    /// </para>
    /// </summary>
    public static List<FriendConfig> SelectNew(IEnumerable<FriendConfig>? existing, IEnumerable<FriendConfig>? scanned)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in existing ?? [])
        {
            if (entry is null || !BackupMergePolicy.IsUsable(entry))
                continue;

            foreach (var key in BackupMergePolicy.IdentityKeys(entry))
                known.Add(key);
        }

        var result = new List<FriendConfig>();

        foreach (var entry in scanned ?? [])
        {
            if (entry is null || !BackupMergePolicy.IsUsable(entry))
                continue;

            var keys = BackupMergePolicy.IdentityKeys(entry).ToList();
            if (keys.Any(known.Contains))
                continue;

            foreach (var key in keys)
                known.Add(key);

            result.Add(entry);
        }

        return result;
    }
}
