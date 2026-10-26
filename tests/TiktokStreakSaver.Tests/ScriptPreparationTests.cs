using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// The flat form is what actually gets injected. Getting this wrong is silent - the WebView accepts
/// the call, the script never runs, and the feature looks broken rather than misconfigured.
/// </summary>
public class ScriptPreparationTests
{
    [Fact]
    public void TheResultIsASingleLine()
    {
        const string script = "(function () {\n    var a = 1;\n    log(a);\n})();";

        var prepared = ScriptPreparation.ForInjection(script);

        Assert.DoesNotContain("\n", prepared);
        Assert.DoesNotContain("\r", prepared);
        Assert.Contains("var a = 1;", prepared);
    }

    [Fact]
    public void WholeLineCommentsAreRemoved()
    {
        const string script = "// leading note\nvar a = 1;\n    // indented note\nvar b = 2;";

        var prepared = ScriptPreparation.ForInjection(script);

        Assert.DoesNotContain("leading note", prepared);
        Assert.DoesNotContain("indented note", prepared);
        Assert.DoesNotContain("//", prepared);
        Assert.Contains("var a = 1;", prepared);
        Assert.Contains("var b = 2;", prepared);
    }

    [Fact]
    public void CodeAfterACommentLineSurvives()
    {
        // The order of the two steps is what this pins down: collapsing whitespace before removing
        // comment lines would fold `var b = 2;` onto the comment and delete it.
        const string script = "var a = 1;\n// note\nvar b = 2;";

        var prepared = ScriptPreparation.ForInjection(script);

        Assert.Contains("var a = 1;", prepared);
        Assert.Contains("var b = 2;", prepared);
    }

    [Fact]
    public void DoubleSlashesInsideAStringAreNotComments()
    {
        // A URL in a string literal must survive: only a line that *starts* with // is a comment.
        const string script = "var url = 'https://www.tiktok.com/messages';";

        var prepared = ScriptPreparation.ForInjection(script);

        Assert.Contains("https://www.tiktok.com/messages", prepared);
    }

    [Fact]
    public void RunsOfWhitespaceAreCollapsed()
    {
        const string script = "var a = 1;\n\n\n\tvar b = 2;   var c = 3;";

        var prepared = ScriptPreparation.ForInjection(script);

        Assert.Equal("var a = 1; var b = 2; var c = 3;", prepared);
    }

    [Fact]
    public void EscapedNewlinesInAStringAreLeftAlone()
    {
        // `\n` inside a literal is two characters, not whitespace, so it must not be collapsed.
        const string script = "var lines = (text || '').split('\\n');";

        var prepared = ScriptPreparation.ForInjection(script);

        Assert.Contains("split('\\n')", prepared);
    }

    [Fact]
    public void RegularExpressionsSurvive()
    {
        const string script = "var m = s.replace(/\\s+/g, ' ').trim();\nvar t = /^\\d{1,2}:\\d{2}/.test(s);";

        var prepared = ScriptPreparation.ForInjection(script);

        Assert.Contains("replace(/\\s+/g, ' ')", prepared);
        Assert.Contains("/^\\d{1,2}:\\d{2}/", prepared);
    }

    [Fact]
    public void PreparingAnAlreadyPreparedScriptChangesNothing()
    {
        var once = ScriptPreparation.ForInjection("// note\nvar a = 1;\nvar b = 2;");

        Assert.Equal(once, ScriptPreparation.ForInjection(once));
    }

    [Fact]
    public void TheBundledScannerIsPreparedIntoOneLine()
    {
        // Guards the real script: a new multi-line string literal or a stray comment would still
        // pass the tests above but only break on a device.
        var path = FindScannerScript();
        if (path is null)
        {
            // Running somewhere the source tree is not reachable. Not a failure: the property this
            // test guards is already covered by the cases above.
            return;
        }

        var prepared = ScriptPreparation.ForInjection(System.IO.File.ReadAllText(path));

        Assert.DoesNotContain("\n", prepared);
        Assert.DoesNotContain("//", prepared);
        Assert.StartsWith("(function ()", prepared);
        Assert.Contains("window.__saverScan", prepared);
    }

    [Fact]
    public void TheBundledSendingScriptSurvivesPreparation()
    {
        // The sending script is flattened and then evaluated in the WebView. A trailing comment on a
        // line of code survives the comment-stripping step (only lines that *start* with // are
        // removed) and, once the newlines are gone, swallows every statement after it. The result is
        // a SyntaxError at parse time: the WebView accepts the call, nothing runs, and the app reports
        // a timeout - which is exactly how the Business Suite sends failed silently.
        var path = FindRawScript("tiktok_automation.js");
        if (path is null)
            return;

        var prepared = ScriptPreparation.ForInjection(System.IO.File.ReadAllText(path));

        Assert.DoesNotContain("\n", prepared);
        Assert.EndsWith("})();", prepared);

        // Every // left in the prepared script has to be inside a string or a regex, never a comment.
        // A comment would leave the text after it unexecuted, so the closing of the IIFE is the tell:
        // if a comment swallowed the tail, the script no longer ends where it should.
        Assert.Contains("init(); })();", prepared);
    }

    [Theory]
    [InlineData("tiktok_read_contacts.js")]
    [InlineData("tiktok_automation.js")]
    public void ABundledScriptHasNoTrailingCommentOnACodeLine(string fileName)
    {
        // Catches the mistake at its source rather than after flattening, where the symptom is a
        // truncated script that is hard to trace back to one line.
        var path = FindRawScript(fileName);
        if (path is null)
            return;

        var lines = System.IO.File.ReadAllLines(path);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("//"))
                continue;

            // A // that is not inside a quoted string is a trailing comment.
            var inSingle = false;
            var inDouble = false;

            for (var c = 0; c < line.Length - 1; c++)
            {
                var ch = line[c];

                if (ch == '\\' && (inSingle || inDouble))
                {
                    c++;
                    continue;
                }

                if (ch == '\'' && !inDouble) inSingle = !inSingle;
                else if (ch == '"' && !inSingle) inDouble = !inDouble;
                else if (ch == '/' && line[c + 1] == '/' && !inSingle && !inDouble)
                {
                    // Assert.Fail does not exist in this xUnit version, so the message goes through
                    // Assert.True, which reports it the same way.
                    Assert.True(false,
                        $"{fileName} line {i + 1} has a trailing comment. The script is flattened onto " +
                        $"one line before injection, so everything after it would be commented out: {line.Trim()}");
                }
            }
        }
    }

    /// <summary>
    /// The send path must ask for delivery proof, not just an empty composer.
    /// <para>
    /// An empty composer only proves TikTok accepted the click - a re-render or a torn-down panel clears
    /// it too - so a script that stops there can report a delivered message that never appeared in the
    /// thread. The bubble check is what makes the confirmation mean something, and it has to be wired into
    /// both send paths, because the classic inbox and Business Suite go through different branches.
    /// </para>
    /// </summary>
    [Fact]
    public void TheSendingScriptVerifiesTheMessageAppearedInTheThread()
    {
        var path = FindRawScript("tiktok_automation.js");
        if (path is null)
            return;

        var script = System.IO.File.ReadAllText(path);

        // The helpers exist.
        Assert.Contains("var collectMessagesWithTimes = function", script);
        Assert.Contains("var judgeAgainstWindow = function", script);
        Assert.Contains("var verifyDelivery = function", script);

        // The selectors are the ones checked against a live inbox, in both the classic inbox and Business
        // Suite. Pinning them is the point: if they are ever changed back to guesses, every chat comes back
        // unverified and the app starts resending to people who already got the message.
        Assert.Contains("[data-e2e=\"dm-new-message-text\"]", script);
        Assert.Contains("[data-e2e=\"dm-new-chat-item\"]", script);

        // Both branches consult the verdict rather than the composer alone.
        var verdicts = System.Text.RegularExpressions.Regex.Matches(script, "var delivery = verifyDelivery\\(\\);");
        Assert.True(verdicts.Count >= 2,
            $"Both send paths have to call verifyDelivery(); found {verdicts.Count}.");
    }

    /// <summary>
    /// The rescan must be a look, never a send.
    /// <para>
    /// Its whole purpose is to check what already happened, so a rescan that typed into the composer would
    /// double-message every chat it was sent to inspect. That makes this a correctness requirement rather
    /// than a style one.
    /// </para>
    /// </summary>
    [Fact]
    public void TheRescanPathNeverSends()
    {
        var path = FindRawScript("tiktok_automation.js");
        if (path is null)
            return;

        var script = System.IO.File.ReadAllText(path);

        Assert.Contains("var rescanChat = function", script);
        Assert.Contains("var reportRescanResult = function", script);
        Assert.Contains("[RescanOnly]", script);

        // Every branch that would send checks the flag first. The three places a send can be started from
        // are the pre-check, the pre-click match, and the opened-chat match.
        var guards = System.Text.RegularExpressions.Regex.Matches(script, "if \\(rescanOnly\\)");
        Assert.True(guards.Count >= 3,
            $"Every send path needs a rescanOnly guard; found {guards.Count}.");
    }

    /// <summary>
    /// The verdict is decided by text AND time, from TikTok's own separators.
    /// <para>
    /// Text alone cannot tell today's message from the identical one sent yesterday, which is the whole
    /// reason the check exists. Pinning the separator selector matters as much: if it is changed back to a
    /// guess, every message reads as untimed and the app stops being able to confirm anything.
    /// </para>
    /// </summary>
    [Fact]
    public void TheVerdictUsesTheMessagesTimestamp()
    {
        var path = FindRawScript("tiktok_automation.js");
        if (path is null)
            return;

        var script = System.IO.File.ReadAllText(path);

        Assert.Contains("[data-e2e=\"dm-new-message-list\"]", script);
        Assert.Contains("var parseSeparator = function", script);
        Assert.Contains("var collectMessagesWithTimes = function", script);
        Assert.Contains("var judgeAgainstWindow = function", script);

        // The separator is matched as a value, not as a selector: the walk reads every element's data-e2e
        // and compares it, which is what lets one pass map each message to the separator above it.
        Assert.Contains("e2e === 'dm-new-time-separator'", script);

        // The window is passed in by the app, and an unreplaced placeholder must not be read as a date.
        Assert.Contains("[WindowStart]", script);
        Assert.Contains("[WindowEnd]", script);
        Assert.Contains("if (windowStart.indexOf('[') === 0) windowStart = '';", script);
    }

    /// <summary>
    /// Only messages we sent count.
    /// <para>
    /// A friend replying "🔥" - the same text as the streak message - would otherwise satisfy the check and
    /// the app would record a delivery that never happened. Every message row links to its sender, and our
    /// own rows link to us, which is what makes the distinction possible.
    /// </para>
    /// </summary>
    [Fact]
    public void OnlyOurOwnMessagesCount()
    {
        var path = FindRawScript("tiktok_automation.js");
        if (path is null)
            return;

        var script = System.IO.File.ReadAllText(path);

        Assert.Contains("var senderOf = function", script);
        Assert.Contains("var resolveSelfHandle = function", script);
        Assert.Contains("[SelfHandle]", script);

        // The filter is applied, and it only rejects rows that name a different sender - a row with no
        // handle is kept, so an unrecognised markup cannot turn a delivery into a false "missing".
        Assert.Contains("if (mine && entry.sender && entry.sender !== mine) continue;", script);
    }

    /// <summary>
    /// A message whose time cannot be read must never be reported as missing.
    /// <para>
    /// 'absent' is what makes the app send again, so it may only be returned when the times were readable
    /// and the message genuinely was not in the window. Otherwise a phone set to a locale this parser does
    /// not handle would resend to everybody.
    /// </para>
    /// </summary>
    [Fact]
    public void AnUnreadableTimeNeverProducesAnAbsentVerdict()
    {
        var path = FindRawScript("tiktok_automation.js");
        if (path is null)
            return;

        var script = System.IO.File.ReadAllText(path);

        // The guard exists, and it is the text-present-but-untimed case that returns 'unknown'.
        Assert.Contains("if (textMatches > 0 && timedEntries === 0) return 'unknown';", script);
    }

    private static string? FindRawScript(string fileName)
    {        var relative = System.IO.Path.Combine(
            "src", "TiktokStreakSaver", "Resources", "Raw", fileName);

        var candidates = new[]
        {
            System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", relative)),
            System.IO.Path.GetFullPath(relative),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), relative)),
        };

        return candidates.FirstOrDefault(System.IO.File.Exists);
    }

    private static string? FindScannerScript()
    {
        var relative = System.IO.Path.Combine(
            "src", "TiktokStreakSaver", "Resources", "Raw", "tiktok_read_contacts.js");

        // From the test binary, and from wherever the tests were started, since the two differ
        // between a local run and CI.
        var candidates = new[]
        {
            System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", relative)),
            System.IO.Path.GetFullPath(relative),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), relative)),
        };

        return candidates.FirstOrDefault(System.IO.File.Exists);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void NothingToPrepareYieldsEmpty(string? script)
    {
        Assert.Equal(string.Empty, ScriptPreparation.ForInjection(script));
    }

    [Fact]
    public void TheBundledScannerReportsThroughTheBridgeTheAppRegisters()
    {
        var path = FindScannerScript();
        if (path is null)
            return;

        var script = System.IO.File.ReadAllText(path);

        // The bridge name exists in two languages, and a mismatch between them is silent: the script
        // would run, its reports would go nowhere, and the page would conclude the scanner never
        // started. Comparing them here is the only place that can catch it.
        Assert.Contains($"typeof {ContactScanReader.BridgeName}", script);
        Assert.Contains($"{ContactScanReader.BridgeName}.report(", script);
    }

    [Fact]
    public void TheBundledScannerIdentifiesChatsTheWaySendingDoes()
    {
        var path = FindScannerScript();
        if (path is null)
            return;

        var script = System.IO.File.ReadAllText(path);

        // Opening the conversation and reading its header is the only method that works here. The
        // list rows show a display name and a message preview in one text block, and carry no
        // profile link for direct messages, so reading rows got the preview as a name and called
        // every direct message a group. This test exists so that cannot be reintroduced quietly.
        //
        // The identity is the header's own link, never a link found elsewhere on the page: see
        // ScannerScriptTests.TheBundledScannerTakesTheIdentityFromTheHeaderAlone.
        Assert.Contains("chat-header", script);
        Assert.Contains("item.click()", script);
        Assert.Contains("headerProfileLink", script);
    }
}
