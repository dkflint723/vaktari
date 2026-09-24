using System.Runtime.Versioning;
using Vaktari.Core;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// A user's .bat or .cmd script, given the selected paths.
///
/// **A file's name was run as a command.** Windows hands a batch file to
/// cmd.exe, which reads the command line again by its own rules, and a name
/// with no space in it went through unquoted: "report&amp;ver" ran ver, and a
/// comma split one name into two paths. These run a real script, and the name
/// that would have been a command is one that makes a folder — so its absence
/// is the proof.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BatchScriptTests
{
    /// <summary>
    /// Writes its first argument, whole, beside itself. Delayed expansion
    /// inside the script, so the script's own line cannot be what runs it.
    /// </summary>
    private static ScriptCommand Script(TempTree tree)
    {
        var path = tree.Write("scripts/echo_first.bat",
            "@echo off\r\n"
            + "setlocal EnableDelayedExpansion\r\n"
            + "set \"v=%~1\"\r\n"
            + ">\"%~dp0out.txt\" echo(!v!\r\n");

        return new ScriptCommand("echo first", path);
    }

    [WindowsFact]
    public async Task A_name_that_reads_as_a_command_arrives_as_one_path()
    {
        using var tree = new TempTree();
        var script = Script(tree);
        var work = tree.Dir("work");
        // No space in it: a name with one was quoted all along, and it is
        // the bare ones cmd.exe read as commands. The comma after mkdir is
        // cmd.exe's own separator, so this makes a folder if it runs.
        var named = Path.Combine(work, "x&mkdir,injected");

        // The WHOLE path, not only the name: with whitespace anywhere in it,
        // .NET quotes the argument itself and this would pass without the fix.
        Assert.False(named.Any(char.IsWhiteSpace), $"the temp path has a space, so this proves nothing: {named}");

        await new WindowsScriptRunner(tree.Dir("state")).RunAsync(script, work, [named], CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(work, "injected")), "the name ran as a command");
        Assert.Equal(named, File.ReadAllText(tree.At("scripts", "out.txt")).TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// **%NAME% is expanded even inside quotes**, so a name holding one is
    /// refused, with the reason, rather than handed over as another file.
    /// </summary>
    [WindowsFact]
    public async Task A_name_with_a_percent_is_refused_not_run()
    {
        using var tree = new TempTree();
        var script = Script(tree);
        var work = tree.Dir("work");

        var said = await new WindowsScriptRunner(tree.Dir("state")).RunAsync(
            script, work, [Path.Combine(work, "a%OS%b.txt")], CancellationToken.None);

        Assert.Contains("was not run", said, StringComparison.Ordinal);
        Assert.False(File.Exists(tree.At("scripts", "out.txt")), "the script ran");
    }
}
