using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **A folded name spelled "\\?\" is handed to nothing either.** A pane opened
/// through "\\?\" lists "t.cmd." and "report " by their own names, and its
/// rows carry the prefix — so every hand-off in the launcher sees
/// "\\?\…\t.cmd.", "\\?\UNC\…\report " and "\??\…" as often as the plain
/// spelling. The rule for a hand-off is <see cref="ReachablePath.RefuseHandedOut"/>,
/// which takes the prefix off before it asks, because what the receiver does
/// with it is its own business: Explorer, and anything else that strips it,
/// opens the neighbour. <see cref="ReachablePath.Refuse"/> lets the prefixed
/// spelling through, rightly, for Vaktari's own reads and acts — so a launcher
/// that asked THAT rule would pass every one of these, and the tests beside
/// this one, all spelled plainly, would not notice (fix8's verification: the
/// mutation stayed green).
///
/// Nothing here starts anything or binds anything: the starter and chooser
/// record, and every refusal happens before a path is parsed. The control is
/// the prefixed ORDINARY neighbour, handed over exactly as spelled.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrailingNameExtendedHandOffTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-extfold").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(@"\\?\" + _root, "*", SearchOption.AllDirectories)) File.Delete(f);
            Directory.Delete(@"\\?\" + _root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private string File_(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(@"\\?\" + path, "@exit");
        return path;
    }

    /// <summary>The same entry, spelled the three ways a row can carry it.</summary>
    private static string[] Prefixed(string path)
        => [@"\\?\" + path, @"\??\" + path, @"\\?\UNC\server\share" + path[2..]];

    private static bool Binds(string path)
    {
        var bound = false;

        var thread = new Thread(() =>
        {
            var data = AssocHandlers.DataObjectFor(path);
            bound = data != IntPtr.Zero;
            if (bound) Marshal.Release(data);
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return bound;
    }

    [WindowsTheory]
    [InlineData("t.cmd.")]
    [InlineData("t.cmd ")]
    public async Task A_prefixed_folded_file_is_opened_run_offered_or_bound_by_nothing(string name)
    {
        File_(name.TrimEnd(' ', '.'));
        var trailing = File_(name);

        var started = new List<string>();
        var chosen = new List<string>();
        var launcher = new WindowsLauncher
        {
            Starter = info => { started.Add(info.FileName + " " + string.Join(" ", info.ArgumentList)); return null; },
            Chooser = path => { chosen.Add(path); return true; },
        };

        foreach (var spelled in Prefixed(trailing))
        {
            Assert.IsType<IOException>(launcher.Open(spelled));
            launcher.OpenElevated(spelled);
            launcher.OpenWith(spelled, new LaunchOption("Nobody", "vaktari-no-such-handler"));
            Assert.False(launcher.ChooseApplication(spelled));

            Assert.False(Binds(spelled), spelled + " was bound for a handler");
            Assert.False(ShellPropertySheet.Accepts(spelled), spelled + " was offered the shell's sheet");
            Assert.Null(new WindowsFileIcons().IconFor(spelled, isDirectory: false, size: 32));

            using var menu = await new WindowsShellMenuProvider().BuildAsync([spelled]);
            Assert.Null(menu);

            Assert.IsType<IOException>(Record.Exception(() => new WindowsShortcuts().CreateShortcut(spelled, _root)));
        }

        Assert.Empty(started);
        Assert.Empty(chosen);
        Assert.Empty(Directory.GetFiles(@"\\?\" + _root, "*.lnk"));
    }

    [WindowsFact]
    public void No_terminal_starts_in_a_prefixed_folded_folder()
    {
        var album = Path.Combine(_root, "album ");
        Directory.CreateDirectory(Path.Combine(_root, "album"));
        Directory.CreateDirectory(@"\\?\" + album);

        var started = new List<ProcessStartInfo>();
        var launcher = new WindowsLauncher { Starter = info => { started.Add(info); return null; } };
        var cmd = new TerminalOption("cmd", "Command Prompt", "cmd.exe", []);

        foreach (var spelled in Prefixed(album))
        {
            launcher.OpenTerminal(spelled);
            launcher.OpenTerminal(spelled, cmd);
            launcher.OpenElevatedTerminal(spelled, cmd);
        }

        Assert.Empty(started);
    }

    /// <summary>The control: the neighbour, prefixed, is an ordinary name and
    /// goes to the shell exactly as it is spelled — the rule adds nothing to a
    /// path and takes nothing off one.</summary>
    [WindowsFact]
    public void A_prefixed_ordinary_file_is_handed_over_as_spelled()
    {
        var neighbour = File_("t.cmd");
        var started = new List<string>();
        var launcher = new WindowsLauncher { Starter = info => { started.Add(info.FileName); return null; } };

        foreach (var spelled in Prefixed(neighbour)) Assert.Null(launcher.Open(spelled));

        Assert.Equal(Prefixed(neighbour), started);
    }
}
