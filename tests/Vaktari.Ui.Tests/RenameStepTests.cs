using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Tab in the rename bar, in the real window.
///
/// Which row is next is <see cref="RenameRunTests"/>' job. This is the half
/// that only a window can answer: whether Tab reaches the bar at all, and the
/// two orderings that decide whether a run is honest — the neighbour picked
/// before the rename re-lists the folder, and the step held until the file
/// system has actually said yes.
/// </summary>
public sealed class RenameStepTests : OwnedViewModels
{
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private sealed record Rig(Window Window, ShellViewModel Shell, TextBox Input, string Root)
        : IDisposable
    {
        public void Dispose()
        {
            // Closing flushes the session, which used to be the developer's own
            // — this class left its temp folders in their back stack, and once
            // left the application opening on a folder that no longer existed.
            // TestState points every store this window builds at a directory
            // belonging to the run, so there is nothing to put back here.
            Window.Close();

            try { Directory.Delete(Root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>A real folder with three real files, because a rename that has
    /// to succeed has to have something to rename.</summary>
    private async Task<Rig> BuildAsync()
    {
        UseSearch(PaneViewModel.Search);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-renamerun-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(Path.Combine(root, name), name);

        var window = new MainWindow();

        window.Show();
        Settle();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        // Awaited, never blocked on: a headless test runs ON the dispatcher, and
        // the load posts its finishing work back to it — so a GetResult here
        // waits for a callback that cannot run until the wait ends.
        await shell.ActiveTab!.NavigateAsync(root);
        Settle();

        var input = window.FindControl<TextBox>("PromptInput");

        Assert.NotNull(input);
        Assert.Equal(3, shell.ActiveTab.Entries.Count);

        return new Rig(window, shell, input!, root);
    }

    /// <summary>The reload a rename starts is not awaited, so a test has to
    /// wait the way the window does.</summary>
    private static async Task Drain()
    {
        for (var i = 0; i < 60; i++)
        {
            Settle();
            await Task.Delay(5);
        }

        Settle();
    }

    private static void Rename(Rig rig, FileEntry row, string to)
    {
        rig.Shell.ActiveTab!.SelectedEntry = row;
        rig.Shell.ActiveTab.BeginRenameCommand.Execute(null);

        Settle();

        Assert.Equal(row.Name, rig.Input.Text);

        rig.Input.Text = to;
        rig.Input.Focus();

        rig.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Settle();
    }

    /// <summary>The whole gesture: rename one, land on the next.</summary>
    [AvaloniaFact]
    public async Task Tab_commits_the_name_and_opens_the_next_one()
    {
        using var rig = await BuildAsync();
        var pane = rig.Shell.ActiveTab!;

        Rename(rig, pane.Entries.Single(e => e.Name == "a.txt"), "one.txt");

        Assert.True(File.Exists(Path.Combine(rig.Root, "one.txt")));
        Assert.Equal("b.txt", rig.Input.Text);
    }

    /// <summary>
    /// **The neighbour is chosen BEFORE the rename lands.** Renaming re-lists
    /// the folder and can re-sort it — "one.txt" sorts after "b.txt" where
    /// "a.txt" sorted before it — so asked afterwards, "the next one" means
    /// whichever file has closed the gap behind the row just finished. Here
    /// that would be c.txt, and b.txt would be skipped in a run of three.
    /// </summary>
    [AvaloniaFact]
    public async Task The_next_one_is_the_row_that_was_next_when_you_pressed()
    {
        using var rig = await BuildAsync();
        var pane = rig.Shell.ActiveTab!;

        Rename(rig, pane.Entries.Single(e => e.Name == "a.txt"), "one.txt");

        Assert.Equal("b.txt", rig.Input.Text);
        Assert.NotEqual("c.txt", rig.Input.Text);
    }

    /// <summary>
    /// **A name the file system refuses stops the run.** The local check
    /// answers the SHAPE of a name and never asks the disk, so the commonest
    /// refusal of all — the name is already taken — gets past it and arrives on
    /// a continuation. Stepping on regardless is how a run skips a file in
    /// silence.
    /// </summary>
    [AvaloniaFact]
    public async Task A_name_already_taken_stops_the_run()
    {
        using var rig = await BuildAsync();
        var pane = rig.Shell.ActiveTab!;

        Rename(rig, pane.Entries.Single(e => e.Name == "a.txt"), "c.txt");

        // Still a.txt on disk, and the bar has not moved on to anything.
        Assert.True(File.Exists(Path.Combine(rig.Root, "a.txt")));
        Assert.NotEqual("b.txt", rig.Input.Text);
    }

    /// <summary>
    /// A name refused before it ever leaves the window keeps the bar open with
    /// the text in it, which is what the existing refusal does — Tab must not
    /// turn that into a step.
    /// </summary>
    [AvaloniaFact]
    public async Task A_name_refused_in_the_box_keeps_the_box()
    {
        using var rig = await BuildAsync();
        var pane = rig.Shell.ActiveTab!;

        Rename(rig, pane.Entries.Single(e => e.Name == "a.txt"), "   ");

        // The premise: the bar really is still open with the refused text in
        // it. Without this the test would pass just as well if the bar had
        // closed, and the guard it exists for would be untested.
        var bar = rig.Window.FindControl<Border>("PromptBar");

        Assert.NotNull(bar);
        Assert.True(bar!.IsVisible, "the refusal did not keep the bar open");

        Assert.Equal("   ", rig.Input.Text);
        Assert.True(File.Exists(Path.Combine(rig.Root, "a.txt")));

        // **And the selection has not moved either.** Stepping on regardless
        // would leave the bar on a.txt — the one-tenant rule sees to that — and
        // the listing's highlight on b.txt, so the two would disagree about
        // which file is being renamed.
        Assert.Equal("a.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>
    /// Shift+Tab goes the other way, and the run stops at the end rather than
    /// wrapping round to a name that has just been settled.
    /// </summary>
    [AvaloniaFact]
    public async Task Shift_tab_goes_back_and_the_run_stops_at_the_edge()
    {
        using var rig = await BuildAsync();
        var pane = rig.Shell.ActiveTab!;

        pane.SelectedEntry = pane.Entries.Single(e => e.Name == "a.txt");
        pane.BeginRenameCommand.Execute(null);
        Settle();

        rig.Input.Focus();
        rig.Window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Settle();

        // Nothing above a.txt, so the run ended: the bar is closed rather than
        // wrapped round to c.txt, and it has not stepped FORWARD to b.txt
        // either, which is what Shift being ignored would do.
        var bar = rig.Window.FindControl<Border>("PromptBar");

        Assert.NotNull(bar);
        Assert.False(bar!.IsVisible, "the run did not end at the top of the listing");
        Assert.NotEqual("b.txt", rig.Input.Text);
    }

    /// <summary>
    /// **The file you had just renamed came back unselected.** The refresh
    /// rebuilds the listing from the file system and the path that was selected
    /// went with the old name, so carrying the selection over restores a row
    /// that is not there any more.
    /// </summary>
    [AvaloniaFact]
    public async Task A_plain_rename_leaves_the_renamed_file_selected()
    {
        using var rig = await BuildAsync();
        var pane = rig.Shell.ActiveTab!;

        pane.SelectedEntry = pane.Entries.Single(e => e.Name == "a.txt");
        pane.BeginRenameCommand.Execute(null);
        Settle();

        rig.Input.Text = "one.txt";
        rig.Input.Focus();
        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle();

        await Drain();

        Assert.Equal("one.txt", pane.SelectedEntry?.Name);
        Assert.Equal("one.txt", Assert.Single(pane.Selection).Name);
    }

    /// <summary>
    /// **The bar edited one file while the listing highlighted another.** The
    /// step picks the next row and opens its bar; a request to select the file
    /// just renamed, registered from inside the rename, lands after that and
    /// takes the highlight back — so F2, Delete and Copy would all have acted
    /// on the row nobody was looking at.
    /// </summary>
    [AvaloniaFact]
    public async Task The_bar_and_the_highlight_name_the_same_file()
    {
        using var rig = await BuildAsync();
        var pane = rig.Shell.ActiveTab!;

        Rename(rig, pane.Entries.Single(e => e.Name == "a.txt"), "one.txt");

        await Drain();

        Assert.Equal("b.txt", rig.Input.Text);
        Assert.Equal("b.txt", pane.SelectedEntry?.Name);
        Assert.Equal("b.txt", Assert.Single(pane.Selection).Name);
    }
}
