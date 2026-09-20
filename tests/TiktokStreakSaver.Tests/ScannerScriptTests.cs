using System.ComponentModel;
using System.Diagnostics;
using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// Checks the shape of the contact scanner, and then runs it.
/// <para>
/// The other tests of this script read it as text, and text cannot tell a helper that lives inside
/// the scanner's scope from one that was written after its closing brace. That gap is not
/// hypothetical: a scanner that threw a ReferenceError on the first chat it opened passed the whole
/// suite and shipped four times, because the tests found the names they looked for without ever
/// asking where those names lived. The structural tests below ask, and the harness executes the
/// script against a stub inbox so a broken call cannot hide behind a present name.
/// </para>
/// </summary>
public class ScannerScriptTests
{
    [Fact]
    public void TheBundledScannerWalksAStubInbox()
    {
        var scanner = FindScanner();
        var harness = FindFile("tests", "TiktokStreakSaver.Tests", "ScannerHarness", "scanner_harness.js");
        if (scanner is null || harness is null)
        {
            // Running somewhere the source tree is not reachable. The structural tests below still
            // cover the scanner.
            return;
        }

        // The prepared script is what the device runs, and the two are not the same file: flattening
        // the script onto one line turns a comment trailing a statement into a comment over everything
        // that follows it. Handing the harness the raw file would leave that difference untested, so it
        // gets exactly what the WebView gets.
        var prepared = ScriptPreparation.ForInjection(System.IO.File.ReadAllText(scanner));
        var preparedPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tiktok_read_contacts_{Guid.NewGuid():N}.js");

        try
        {
            System.IO.File.WriteAllText(preparedPath, prepared);
            var result = RunNode(harness, preparedPath);
            if (result is null)
            {
                // No Node.js here. Skipped rather than failed, because the scanner does not need it and
                // the structural tests still guard its shape.
                return;
            }

            Assert.True(result.Value.ExitCode == 0, result.Value.Output);

            // The harness finished rather than giving up, and nothing in it failed.
            Assert.Contains("RESULT scenarios=", result.Value.Output);
            Assert.DoesNotContain("FAILED", result.Value.Output);

            // The failures that reached a device, named here so they are visible in this file: a group
            // must stay a group, a header that is still loading must not be recorded as "@", and a name
            // of invisible filler must not become a contact with no name.
            Assert.Contains("CHAT @alice \"Alice\"", result.Value.Output);
            Assert.Contains("CHAT group \"Weekend Crew\"", result.Value.Output);
            Assert.Contains("CHAT group \"Humas SMKN 1 TROWULAN\"", result.Value.Output);
            Assert.Contains("CHAT @xznsenpai \"xznsenpai\"", result.Value.Output);
        }
        finally
        {
            try { System.IO.File.Delete(preparedPath); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheBundledScannerEndsWithItsIife()
    {
        var script = ReadScanner();
        if (script is null)
            return;

        // Anything written after the closing brace sits outside the scanner's scope, where findHeader,
        // scan and log do not exist. The calls that reach them then throw at runtime from a timer
        // callback, so the scan stops after opening one chat and the page reports only "Script error."
        // That is exactly what shipped from 2.1.14 through 2.1.18.
        var prepared = ScriptPreparation.ForInjection(script);

        Assert.StartsWith("(function ()", prepared);
        Assert.EndsWith("})();", prepared);
        Assert.Equal(1, prepared.Split("})();").Length - 1);
    }

    [Fact]
    public void TheBundledScannerTakesTheIdentityFromTheHeaderAlone()
    {
        var script = ReadScanner();
        if (script is null)
            return;

        // A group's message pane links to the members who wrote, so searching the page for a profile
        // link when the header has none is what gave every group a member's username and imported it
        // as a friend. The scanner must never look outside the header for one.
        Assert.DoesNotContain("document.querySelectorAll('a[href*=\"/@\"]')", script);
        Assert.Contains("headerProfileLink", script);

        // A header read before the account loads is a bare "@", and a chat filed under that name can
        // never be found again.
        Assert.Contains("isUsableName", script);
    }

    [Theory]
    [InlineData("findHeader")]
    [InlineData("headerProfileLink")]
    [InlineData("distinctProfileLinks")]
    [InlineData("avatarCount")]
    [InlineData("usernameFrom")]
    [InlineData("isBlankChar")]
    [InlineData("isUsableName")]
    [InlineData("isUsableUsername")]
    [InlineData("slugOf")]
    [InlineData("looksGeneratedFromName")]
    [InlineData("headerName")]
    [InlineData("groupReason")]
    [InlineData("identifyOpenChat")]
    [InlineData("keyOf")]
    [InlineData("finish")]
    [InlineData("settle")]
    [InlineData("record")]
    [InlineData("readHeader")]
    [InlineData("namelessPerson")]
    [InlineData("headerFacts")]
    [InlineData("headerReport")]
    [InlineData("missChat")]
    public void TheBundledScannerDeclaresEachHelperInsideItsIife(string helper)
    {
        var script = ReadScanner();
        if (script is null)
            return;

        var prepared = ScriptPreparation.ForInjection(script);
        var close = prepared.LastIndexOf("})();", StringComparison.Ordinal);
        var declaration = prepared.IndexOf($"var {helper} = function", StringComparison.Ordinal);

        Assert.True(declaration >= 0, $"The scanner no longer declares {helper}.");
        Assert.True(declaration < close, $"{helper} is declared outside the scanner's scope.");
    }

    private static string? ReadScanner()
    {
        var path = FindScanner();
        return path is null ? null : System.IO.File.ReadAllText(path);
    }

    private static string? FindScanner() => FindFile(
        "src", "TiktokStreakSaver", "Resources", "Raw", "tiktok_read_contacts.js");

    private static string? FindFile(params string[] relativeParts)
    {
        var relative = System.IO.Path.Combine(relativeParts);

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

    /// <summary>
    /// Runs the harness under Node.js, or returns null when Node.js is not installed.
    /// </summary>
    private static (int ExitCode, string Output)? RunNode(string harness, string scanner)
    {
        var info = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(harness);
        info.ArgumentList.Add(scanner);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;

            // Started before waiting, so a harness that writes more than a pipe buffer cannot block.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                return (124, "the harness did not finish");
            }

            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
