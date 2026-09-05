using System.Runtime.Versioning;
using Microsoft.Win32;
using Vaktari.Windows;
using Xunit;
using Xunit.Abstractions;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Reading the machine's own context menu.
///
/// **These assert against whatever is installed on the machine running them**,
/// which is unusual here and is the point: the entries under test are supplied
/// by third-party shell extensions, so a fixture proving we can read a menu we
/// built ourselves would prove nothing about the thing that actually breaks.
/// What is asserted is therefore structural — that the shell answered, that the
/// rows have text and ids, that submenus came back — rather than "7-Zip is
/// present", which would fail on a machine without it.
///
/// The one exception is the diagnostic test, which writes what it found to the
/// test output so the list can be read by eye. That is how this was verified in
/// the first place.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellContextMenuTests : IDisposable
{
    /// <summary>Long enough that a loaded agent never trips it, short enough
    /// that a regression is a red test inside the minute.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly ITestOutputHelper _output;
    private readonly string _folder;
    private readonly string _file;

    public ShellContextMenuTests(ITestOutputHelper output)
    {
        _output = output;

        // Its own directory, made here and removed after: the shell is being
        // asked about these paths and a handler may well look at them.
        _folder = Path.Combine(Path.GetTempPath(), "vaktari-shellmenu-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_folder);

        _file = Path.Combine(_folder, "sample.txt");
        File.WriteAllText(_file, "sample");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    [Fact]
    public async Task The_shell_offers_a_menu_for_a_file()
    {
        using var menu = await ShellContextMenu.ForAsync([_file]);

        Assert.NotNull(menu);
        Assert.NotEmpty(menu!.Entries);
    }

    [Fact]
    public async Task The_shell_offers_a_menu_for_a_folder()
    {
        using var menu = await ShellContextMenu.ForAsync([_folder]);

        Assert.NotNull(menu);
        Assert.NotEmpty(menu!.Entries);
    }

    /// <summary>
    /// Every row is usable: a label to draw and an id to invoke. A blank row or
    /// a negative id would be an entry the user can see and click to no effect,
    /// which is the failure mode this whole feature has to avoid.
    /// </summary>
    [Fact]
    public async Task Every_entry_has_something_to_draw_and_something_to_invoke()
    {
        using var menu = await ShellContextMenu.ForAsync([_file]);
        Assert.NotNull(menu);

        foreach (var entry in menu!.Entries.Where(e => !e.IsSeparator))
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Label), "an entry had no text");
            Assert.True(entry.Id >= 0, $"'{entry.Label}' had id {entry.Id}");
        }
    }

    /// <summary>
    /// The shell's menu is built to be merged into another one, so it happily
    /// starts or ends with a rule and puts two together. Avalonia draws every
    /// separator it is given and collapses none — a defect this project already
    /// shipped once in its own menu, and there is no reason to ship it again
    /// with somebody else's.
    /// </summary>
    [Fact]
    public async Task No_rule_is_left_drawing_against_nothing()
    {
        using var menu = await ShellContextMenu.ForAsync([_file]);
        Assert.NotNull(menu);

        var entries = menu!.Entries;

        Assert.False(entries.Count > 0 && entries[0].IsSeparator, "leads with a rule");
        Assert.False(entries.Count > 0 && entries[^1].IsSeparator, "ends with a rule");

        for (var i = 1; i < entries.Count; i++)
            Assert.False(entries[i].IsSeparator && entries[i - 1].IsSeparator, $"two rules at {i}");
    }

    /// <summary>
    /// Ampersands are the shell's accelerator marks, not text. Left in, every
    /// other label would read "Cu&amp;t" — and this is the sort of thing that is
    /// obvious in a screenshot and invisible in a passing test.
    /// </summary>
    [Fact]
    public async Task Accelerator_marks_are_not_shown_as_text()
    {
        using var menu = await ShellContextMenu.ForAsync([_file]);
        Assert.NotNull(menu);

        Assert.All(menu!.Entries, e => Assert.DoesNotContain('&', e.Label));
    }

    /// <summary>
    /// A selection of several must reach the handlers as several, or "add to
    /// archive" on eight files quietly makes an archive of one.
    /// </summary>
    [Fact]
    public async Task A_selection_of_several_still_produces_a_menu()
    {
        var second = Path.Combine(_folder, "another.txt");
        File.WriteAllText(second, "another");

        using var menu = await ShellContextMenu.ForAsync([_file, second]);

        Assert.NotNull(menu);
        Assert.NotEmpty(menu!.Entries);
    }

    [Fact]
    public async Task Nothing_selected_means_no_menu()
    {
        Assert.Null(await ShellContextMenu.ForAsync([]));
    }

    /// <summary>
    /// **The fault, pinned at the line it lived on.** The build used to be given
    /// four seconds and then answered as though the shell had offered nothing.
    /// The tests above cannot notice that: this machine's shell answers in about
    /// a tenth of a second, so they pass with a deadline and pass without one.
    /// Measured — putting `.WaitAsync(TimeSpan.FromSeconds(4))` back on the
    /// await in ForAsync reddens this test and nothing else in the project; the
    /// other 393 pass with the fault reinstated, which is why this had to be
    /// written.
    ///
    /// **Five seconds of wall clock, and they are the price of the constant.**
    /// A test cannot tell a reinstated four-second deadline from no deadline
    /// unless it outlasts four seconds; a shorter hold only re-proves what the
    /// tests above already prove. It is the one test in this repository allowed
    /// to be slow, and it is slow for a reason it can name.
    /// </summary>
    [Fact]
    public async Task A_build_that_outruns_the_old_deadline_is_still_the_answer()
    {
        using var held = new ManualResetEventSlim();

        var opening = ShellContextMenu.ForAsync([_file], _ =>
        {
            held.Wait();
            return [new Vaktari.Core.FileSystem.ShellMenuEntry("Late", 0)];
        });

        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.False(opening.IsCompleted, "the shell was given up on while it read");

        held.Set();

        using var menu = await opening.WaitAsync(Patience);

        Assert.NotNull(menu);
        Assert.Equal("Late", menu!.Entries[0].Label);
    }

    /// <summary>
    /// **Closing gives the handles back, and nothing else can see that it did.**
    /// The menu handle and the IContextMenu reference are private IntPtrs that
    /// never leave the apartment thread, so a Dispose that quietly stopped
    /// freeing them would leak one of each per right-click and be invisible —
    /// measured: deleting `_worker.Post(Release);` from Dispose reddens this
    /// test and nothing else, with the other 393 passing while nothing is
    /// freed at all.
    ///
    /// Bounded rather than awaited: the release is queued behind whatever the
    /// worker is already doing, so this waits for the apartment thread to get
    /// to it rather than assuming it already has.
    /// </summary>
    [Fact]
    public async Task Closing_the_menu_gives_the_native_handles_back()
    {
        var menu = await ShellContextMenu.ForAsync([_file]);

        Assert.NotNull(menu);
        Assert.False(menu!.HandlesReleased, "the live menu was holding nothing");

        menu.Dispose();

        Assert.True(
            SpinWait.SpinUntil(() => menu.HandlesReleased, Patience),
            "the menu handle and the COM reference were never given back");
    }

    /// <summary>
    /// **The fault, at the line it lived on.** A right-click on the empty space
    /// inside a folder was handed that folder's ITEM menu — the menu its row
    /// carries in the parent listing — because this file had exactly one way to
    /// bind a menu: SHParseDisplayName into a shell item array, asked for the
    /// items' UI object. The background menu comes from somewhere else
    /// entirely, IShellFolder::CreateViewObject, and nothing here reached it.
    ///
    /// **Asserted as a difference, because the contents are the machine's.**
    /// What each menu holds depends on what is installed, so naming an entry
    /// would pin this test to this desktop. What cannot be a coincidence is
    /// that the two differ: measured with the background bound the item way,
    /// the two label lists came back equal and this test failed on that.
    ///
    /// Measured here, on that temporary directory: the item menu offered Pin to
    /// Quick access, Restore previous versions, Send to and Create shortcut and
    /// the background menu none of those four; the background menu offered the
    /// New submenu and the item menu had no equivalent; and the background menu
    /// was much the shorter of the two. They overlap — both carry this
    /// machine's "open a shell here" handlers — so this asserts they are not
    /// the same menu rather than that they share nothing.
    /// </summary>
    [Fact]
    public async Task The_background_of_a_folder_is_not_the_folders_own_menu()
    {
        using var item = await ShellContextMenu.ForAsync([_folder]);
        using var background = await ShellContextMenu.ForBackgroundAsync(_folder);

        Assert.NotNull(item);

        // Not null is already "the shell offered something": ForAsync answers
        // null when the entries come back empty.
        Assert.NotNull(background);

        Assert.NotEqual(
            item!.Entries.Select(e => e.Label).ToList(),
            background!.Entries.Select(e => e.Label).ToList());

        // And in the direction that says the background is a menu of its own
        // rather than a subset that happened to lose rows.
        Assert.NotEmpty(background.Entries
            .Select(e => e.Label)
            .Except(item.Entries.Select(e => e.Label)));
    }

    /// <summary>
    /// What the marker holds, or null while it cannot be read yet.
    ///
    /// **Existing is not the same as finished, and waiting on the name was
    /// waiting on the wrong thing.** MEASURED: GitHub Actions windows-latest,
    /// run 33943817000, this test failed with "The process cannot access the
    /// file ... ran.txt because it is being used by another process".
    /// `cmd.exe /c echo %V> file` creates the file the moment it opens the
    /// redirect and holds it exclusively until it exits, so a spin that stops
    /// at File.Exists can hand the read a file whose writer has not let go.
    ///
    /// Waiting for content as well as for a successful open: an empty read is
    /// the same race one instant later.
    /// </summary>
    private static string? Finished(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Where a per-user verb for a folder's empty space is
    /// registered. The scratch key below is written under the current user
    /// only, and removed again whatever the test does.</summary>
    private const string BackgroundVerbs = @"Software\Classes\Directory\Background\shell";

    /// <summary>
    /// **An entry on the background menu has to RUN, and none of them did.**
    /// A verb registered under Directory\Background writes the folder into its
    /// command line as %V or %W, and the shell resolves both from the directory
    /// the invoke names. Measured with that field left null: %V, "%V" and %W
    /// all came back ERROR_NO_APPLICATION_ASSOCIATED, which is 0x80070483, and
    /// the command never ran — while a background verb whose command carried no
    /// substitution ran fine, which is what said the folder was the missing
    /// piece. Every background verb registered on this machine is one or the
    /// other, six writing "%V" and WizTree "%W", so the menu was drawing seven
    /// rows that did nothing. The item menu never had the fault: measured, an
    /// item verb's %V is the item, and the item is in that menu's own binding.
    ///
    /// **Its own verb rather than one of the machine's**, for the reason the
    /// class comment gives — asserting that Open PowerShell window here works
    /// would pin this test to a machine that has it. A scratch key under the
    /// current user's classes, written here and deleted in the finally, is a
    /// background verb of exactly the shape that failed, on any machine.
    /// </summary>
    [Fact]
    public async Task A_background_verb_is_told_which_folder_it_is_in()
    {
        const string Verb = "Vaktari.Test.RunsHere";

        var marker = Path.Combine(_folder, "ran.txt");

        using (var key = Registry.CurrentUser.CreateSubKey($@"{BackgroundVerbs}\{Verb}\command"))
            key!.SetValue(null, $"cmd.exe /c echo %V> \"{marker}\"");

        try
        {
            using var menu = await ShellContextMenu.ForBackgroundAsync(_folder);
            Assert.NotNull(menu);

            var entry = Assert.Single(menu!.Entries, e => e.Label == Verb);

            menu.Invoke(entry.Id);

            string? wrote = null;

            Assert.True(
                SpinWait.SpinUntil(
                    () => Finished(marker) is { Length: > 0 } text && (wrote = text) is not null,
                    Patience),
                "the background entry was clicked and nothing happened");

            // And it ran in THIS folder, which is what %V was asking for.
            Assert.Equal(_folder, wrote!.Trim());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"{BackgroundVerbs}\{Verb}", throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// **A submenu the shell fills only when it is about to be shown.** The
    /// walk reads a menu handle rather than displaying one, so an extension
    /// that populates itself on WM_INITMENUPOPUP had never been asked to.
    /// Measured on this machine with the forwarding taken out: the background
    /// menu's New came back holding exactly one row, itself labelled New and
    /// drawn enabled, and invoking that row left the folder empty — an entry
    /// the user can see, open and click to no effect, which is what
    /// <see cref="Every_entry_has_something_to_draw_and_something_to_invoke"/>
    /// says this feature must not do. Forwarding the message through
    /// IContextMenu2 fills it with nine rows — Folder, Shortcut, a rule and six
    /// document types — and Text Document then makes a file.
    ///
    /// **Counted rather than named.** What is in New depends on what is
    /// installed, so what is asserted is the thing that cannot be a
    /// coincidence: a submenu holding more than the one placeholder row.
    /// </summary>
    [Fact]
    public async Task A_submenu_the_shell_fills_on_demand_is_asked_to_fill()
    {
        using var menu = await ShellContextMenu.ForBackgroundAsync(_folder);
        Assert.NotNull(menu);

        var submenus = menu!.Entries
            .Where(e => e.HasChildren)
            .Select(e => e.Items.Count(c => !c.IsSeparator))
            .ToList();

        Assert.NotEmpty(submenus);
        Assert.Contains(submenus, rows => rows > 1);
    }

    /// <summary>
    /// **The folder the background binder borrows, given back.** SHBindToObject
    /// hands out a reference this code owns, and nothing outside the binder can
    /// see a local pointer — the same blindness
    /// <see cref="Closing_the_menu_gives_the_native_handles_back"/> exists for.
    /// Measured: inverting that one line to `if (bound == IntPtr.Zero)` reddens
    /// this test and nothing else — the other 458 pass while each right-click
    /// on empty space leaks a COM reference to an IShellFolder.
    ///
    /// Reads the count Release itself returned, because that number cannot be
    /// there unless the release ran.
    /// </summary>
    [Fact]
    public async Task Binding_a_background_gives_the_folder_back()
    {
        using var menu = await ShellContextMenu.ForBackgroundAsync(_folder);

        Assert.NotNull(menu);
        Assert.NotNull(menu!.FolderReleasedAt);
    }

    /// <summary>
    /// The seam the view model actually calls, one line wide: "ask for a
    /// background" has to arrive at the code that reads one.
    ///
    /// **Nothing in this project covered <see cref="WindowsShellMenuProvider"/>
    /// at all**, so a provider that forwarded both of the shell's questions to
    /// the same place would leave every other test here green with the fault
    /// back in the product.
    /// </summary>
    [Fact]
    public async Task The_provider_forwards_a_background_as_a_background()
    {
        var provider = new WindowsShellMenuProvider();

        using var item = await provider.BuildAsync([_folder]);
        using var background = await provider.BuildBackgroundAsync(_folder);

        Assert.NotNull(item);
        Assert.NotNull(background);

        Assert.NotEqual(
            item!.Entries.Select(e => e.Label).ToList(),
            background!.Entries.Select(e => e.Label).ToList());
    }

    /// <summary>
    /// A path with no folder behind it is no menu — never a menu for somewhere
    /// else. Both kinds reach here: a pane on This PC has `vaktari:computer`
    /// for a CurrentPath and still has empty space to right-click on, and a
    /// folder can be deleted while its pane is open.
    ///
    /// **Two cases because the shell refuses them in two different places, and
    /// only measuring said which.** SHParseDisplayName answers S_OK for
    /// `vaktari:computer` — it reads the colon as a protocol and hands back an
    /// id whose ITEM menu is a lone "Create shortcut" — so what refuses there
    /// is the bind, one step later. A path that is simply not on the disk fails
    /// the parse itself, 0x80070002, and that is the case the HRESULT check
    /// exists for.
    /// </summary>
    [Fact]
    public async Task A_path_with_no_folder_behind_it_offers_no_background_menu()
    {
        Assert.Null(await ShellContextMenu.ForBackgroundAsync("vaktari:computer"));
        Assert.Null(await ShellContextMenu.ForBackgroundAsync(Path.Combine(_folder, "gone")));
    }

    /// <summary>
    /// The same readout as below, for the other menu: what this machine offers
    /// on empty space. Run with the test output visible.
    /// </summary>
    [Fact]
    public async Task What_this_machine_offers_on_empty_space()
    {
        using var menu = await ShellContextMenu.ForBackgroundAsync(_folder);
        Assert.NotNull(menu);

        foreach (var entry in menu!.Entries)
        {
            _output.WriteLine(entry.IsSeparator
                ? "  ---"
                : $"  [{entry.Id,3}] {entry.Label}{(entry.HasChildren ? " >" : "")}");

            foreach (var child in entry.Items)
                _output.WriteLine($"          [{child.Id,3}] {child.Label}");
        }
    }

    /// <summary>
    /// Not an assertion — a readout. Run with the test output visible to see
    /// exactly what this machine's shell hands back, which is how the feature
    /// was verified and how it should be checked again after any change.
    /// </summary>
    [Fact]
    public async Task What_this_machine_offers()
    {
        using var menu = await ShellContextMenu.ForAsync([_file]);
        Assert.NotNull(menu);

        foreach (var entry in menu!.Entries)
        {
            _output.WriteLine(entry.IsSeparator
                ? "  ---"
                : $"  [{entry.Id,3}] {entry.Label}{(entry.HasChildren ? " >" : "")}");

            foreach (var child in entry.Items)
                _output.WriteLine($"          [{child.Id,3}] {child.Label}");
        }
    }
}
