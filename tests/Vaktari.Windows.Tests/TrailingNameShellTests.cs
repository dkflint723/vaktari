using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **Nothing is handed to the shell by a name its parser folds.** Measured in
/// the seventh review round's hunt: ShellExecute of "…\t.cmd." ran the
/// neighbour "t.cmd" (and "t.cmd " ran nothing, saying nothing), and
/// SHCreateItemFromParsingName and SHParseDisplayName both bound "…\report "
/// to "…\report" — which is how Open with, the properties sheet with its
/// read-only and hidden boxes, and the hosted Windows menu reach a file. A
/// terminal given such a folder opened in its neighbour; a shortcut was made
/// to the neighbour; a row drew the neighbour's icon.
///
/// Each of those is asked here directly, against the real shell where asking
/// starts nothing — a context menu is built and never invoked, a data object
/// is bound and released, an icon is read — and through a recording starter
/// or chooser where the real one would start a program or put up a dialog.
/// Every file is in a temporary folder, the trailing names made through
/// "\\?\". Each refusal has a control beside it: the same call with the
/// neighbour's own name goes through, so the silence is the refusal and not a
/// call that could never have happened.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrailingNameShellTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-shellfold").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(@"\\?\" + _root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }

            Directory.Delete(@"\\?\" + _root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }

    private string File_(string name, string content = "x")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(@"\\?\" + path, content);
        return path;
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(@"\\?\" + path);
        return path;
    }

    private static void Refused(string name, Exception? failure)
        => Assert.Contains($"\"{name}\" cannot be handed to another program",
                           Failures.Describe(Assert.IsType<IOException>(failure), "open that file"),
                           StringComparison.Ordinal);

    // ---- ShellExecute: Open, Run as administrator, Open with's fallback --------

    [WindowsTheory]
    [InlineData("t.cmd.")]
    [InlineData("t.cmd..")]
    [InlineData("t.cmd ")]
    public void Open_hands_the_shell_nothing_for_a_folded_name_and_says_why(string name)
    {
        var neighbour = File_("t.cmd", "@echo NEIGHBOUR");
        var trailing = File_(name, "@echo TRAILING");
        var started = new List<string>();
        var launcher = new WindowsLauncher { Starter = info => { started.Add(info.FileName); return null; } };

        Refused(name, launcher.Open(trailing));
        Assert.Empty(started);

        Assert.Null(launcher.Open(neighbour));
        Assert.Equal([neighbour], started);
    }

    /// <summary>The window opens its links through the same call, and a web
    /// address is not a path: one whose last part ends in a dot is still
    /// handed over.</summary>
    [WindowsFact]
    public void A_web_address_is_still_opened()
    {
        var started = new List<string>();
        var launcher = new WindowsLauncher { Starter = info => { started.Add(info.FileName); return null; } };

        Assert.Null(launcher.Open("https://example.com/v1.2."));
        Assert.Equal(["https://example.com/v1.2."], started);
    }

    [WindowsFact]
    public void Run_as_administrator_and_open_with_hand_the_shell_nothing_for_a_folded_name()
    {
        var neighbour = File_("setup.cmd", "@echo NEIGHBOUR");
        var trailing = File_("setup.cmd.", "@echo TRAILING");
        var started = new List<string>();
        var launcher = new WindowsLauncher { Starter = info => { started.Add(info.FileName + " " + string.Join(" ", info.ArgumentList)); return null; } };

        // A handler id nothing registers: Invoke finds no match and the picker
        // fallback is what would run — through the starter, so nothing does.
        var option = new LaunchOption("Nobody", "vaktari-no-such-handler");

        launcher.OpenElevated(trailing);
        launcher.OpenWith(trailing, option);
        Assert.Empty(started);

        launcher.OpenElevated(neighbour);
        launcher.OpenWith(neighbour, option);
        Assert.Equal(2, started.Count);
    }

    [WindowsFact]
    public void The_system_chooser_is_not_shown_for_a_folded_name()
    {
        var neighbour = File_("report", "NEIGHBOUR");
        var trailing = File_("report ", "TRAILING");
        var shown = new List<string>();
        var launcher = new WindowsLauncher { Chooser = path => { shown.Add(path); return true; } };

        Assert.False(launcher.ChooseApplication(trailing));
        Assert.Empty(shown);

        Assert.True(launcher.ChooseApplication(neighbour));
        Assert.Equal([neighbour], shown);
    }

    [WindowsFact]
    public void No_terminal_is_started_in_a_folded_folder()
    {
        var neighbour = Folder("album");
        var trailing = Folder("album ");
        var started = new List<ProcessStartInfo>();
        var launcher = new WindowsLauncher { Starter = info => { started.Add(info); return null; } };
        var cmd = new Core.FileSystem.TerminalOption("cmd", "Command Prompt", "cmd.exe", []);

        launcher.OpenTerminal(trailing);
        launcher.OpenTerminal(trailing, cmd);
        launcher.OpenElevatedTerminal(trailing, cmd);
        Assert.Empty(started);

        launcher.OpenTerminal(neighbour, cmd);
        Assert.Contains(started, info => info.WorkingDirectory == neighbour);
    }

    // ---- the shell's parser: Open with's data object, the sheet, the menu --------

    /// <summary>On an apartment of its own, as the launcher binds it.</summary>
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

    [WindowsFact]
    public void Open_with_binds_no_data_object_for_a_folded_name()
    {
        var neighbour = File_("report.txt", "NEIGHBOUR");
        var trailing = File_("report.txt.", "TRAILING");

        Assert.False(Binds(trailing), "a handler would have been handed report.txt");
        Assert.True(Binds(neighbour), "the control: the shell binds an ordinary name");
    }

    [WindowsFact]
    public void The_properties_sheet_is_not_asked_for_a_folded_name()
    {
        var neighbour = File_("report", "NEIGHBOUR");
        var trailing = File_("report ", "TRAILING");

        Assert.False(ShellPropertySheet.Accepts(trailing), "the sheet would have been report's, read-only box and all");
        Assert.True(ShellPropertySheet.Accepts(neighbour));
    }

    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_windows_menu_is_built_for_a_folded_name(bool background)
    {
        var neighbour = background ? Folder("album") : File_("report", "NEIGHBOUR");
        var trailing = background ? Folder("album ") : File_("report ", "TRAILING");
        var provider = new WindowsShellMenuProvider();

        async Task<IShellMenu?> Build(string path)
            => background ? await provider.BuildBackgroundAsync(path) : await provider.BuildAsync([path]);

        using (var refused = await Build(trailing)) Assert.Null(refused);

        using var built = await Build(neighbour);
        Assert.NotNull(built);
    }

    /// <summary>A selection with one such name in it gets no menu at all: one
    /// built for the rest would act on fewer items than were chosen.</summary>
    [WindowsFact]
    public async Task A_selection_holding_one_folded_name_gets_no_windows_menu()
    {
        var plain = File_("notes.txt", "plain");
        var trailing = File_("report ", "TRAILING");
        File_("report", "NEIGHBOUR");

        using var menu = await new WindowsShellMenuProvider().BuildAsync([plain, trailing]);

        Assert.Null(menu);
    }

    // ---- a row's icon --------------------------------------------------------------

    /// <summary>
    /// An extensionless file is asked of the shell by path, so "report " beside
    /// a FOLDER "report" drew a folder. Measured without the guard: the file
    /// row came back with the folder's pixels.
    /// </summary>
    [WindowsFact]
    public void A_folded_row_draws_no_icon_of_its_neighbour()
    {
        var neighbour = Folder("report");
        var trailing = File_("report ", "TRAILING");
        var icons = new WindowsFileIcons();

        Assert.Null(icons.IconFor(trailing, isDirectory: false, size: 48));
        Assert.NotNull(icons.IconFor(neighbour, isDirectory: true, size: 48));
    }

    // ---- a shortcut ------------------------------------------------------------------

    [WindowsFact]
    public void No_shortcut_is_made_to_a_folded_name_or_into_a_folded_folder()
    {
        var neighbour = File_("report", "NEIGHBOUR");
        var trailing = File_("report ", "TRAILING");
        var folded = Folder("links ");
        Folder("links");
        var shortcuts = new WindowsShortcuts();

        Refused("report ", Record.Exception(() => shortcuts.CreateShortcut(trailing, _root)));

        var into = Assert.IsType<IOException>(Record.Exception(() => shortcuts.CreateShortcut(neighbour, folded)));
        Assert.Contains("\"links \" ends with a space", into.Message, StringComparison.Ordinal);

        Assert.Empty(Directory.GetFiles(@"\\?\" + _root, "*.lnk", SearchOption.AllDirectories));

        // The control: the neighbour by its own name, into a plain folder.
        Assert.EndsWith("report - Shortcut.lnk", shortcuts.CreateShortcut(neighbour, _root), StringComparison.Ordinal);
    }
}
