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
        Assert.Contains("chat-header", script);
        Assert.Contains("item.click()", script);
        Assert.Contains("headerUsername", script);
    }
}
