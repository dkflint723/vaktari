using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Core.Vcs;
using Vaktari.Tests;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The rename-while-open QA's own cases: a real headless window, the
/// platform's own engine and watchers, and another program played by
/// Vaktari.LockHolder.
///
/// The existing hand-over tests prove most rules on a counting file system.
/// These are the shapes the QA wanted proved on the real thing:
/// - a load still reading when the hold begins;
/// - a tab restored from a session and never looked at;
/// - a real git status above the folder;
/// - a failed rename after which every pane, in two windows, still hears a
///   file arrive;
/// - a batch rename stopped by a held file, which is undone in two steps;
/// - a cut, then a rename, then the paste;
/// - names that only start the same.
/// </summary>
public sealed class RenameQaTests : OwnedViewModels
{
    private readonly IVersionControl? _vcsBefore = PaneViewModel.Vcs;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-rename-qa-" + Guid.NewGuid().ToString("N")[..10]);

    public RenameQaTests() => Directory.CreateDirectory(_root);

    public override void Dispose()
    {
        base.Dispose();

        PaneViewModel.Vcs = _vcsBefore;
        CutMarks.Clear();

        try
        {
            // git marks its objects read-only on Windows.
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception) { /* a temp dir is not worth failing over */ }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static async Task Until(Func<bool> condition, string because, int seconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            Settle();
            await Task.Delay(5);
        }

        Settle();

        Assert.True(condition(), because);
    }

    private static void CloseAll(MainWindow founder)
    {
        foreach (var window in founder.Services.Windows.ToList().AsEnumerable().Reverse())
        {
            try { window.Close(); }
            catch (Exception ex) { Vaktari.Core.Quiet.Swallowed("test-teardown", ex); }
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (founder.Services.Windows.Count > 0 && DateTime.UtcNow < deadline)
        {
            Settle();
            Thread.Sleep(1);
        }
    }

    private async Task<MainWindow> OpenAsync()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();
        window.Show();
        Settle();

        await window.Shell.ActiveTab!.NavigateAsync(_root);
        await window.Shell.ActiveTab.RefreshAsync();
        await Until(() => window.Shell.ActiveTab.IsLoaded, "the window never listed the folder");

        return window;
    }

    private static async Task<FileEntry> Row(PaneViewModel pane, string name)
    {
        await Until(() => pane.Entries.Any(e => e.Name == name), $"{name} never listed in {pane.CurrentPath}");
        return pane.Entries.Single(e => e.Name == name);
    }

    /// <summary>A file made after the pane loaded arrives as a row, which only
    /// a running watcher can deliver.</summary>
    private static async Task Watching(PaneViewModel pane, string because)
    {
        var name = "late-" + Guid.NewGuid().ToString("N")[..6] + ".txt";

        File.WriteAllText(Path.Combine(pane.CurrentPath, name), "x");

        await Until(() => pane.Entries.Any(e => e.Name == name), because);
    }

    // ---- a failed rename, two windows ------------------------------------------

    /// <summary>
    /// **A rename refused by another program leaves every pane watching.**
    ///
    /// The hold let go of three tabs inside the folder before the engine
    /// tried: a background tab here, the right half of a split, and a tab in a
    /// second window. Another program then kept the rename from happening.
    /// Every one of those panes, and the pane the rename was asked in, must
    /// still hear a file created afterwards. Rows alone would not prove it: a
    /// reload could supply them, so the file is made after the panes have
    /// settled.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task Qa_a_refused_rename_leaves_every_pane_in_both_windows_watching()
    {
        Directory.CreateDirectory(At("photos", "2026", "june"));

        var window = await OpenAsync();
        var holder = AnotherProgram.HoldingFile(At("photos", "held.txt"));

        try
        {
            var shell = window.Shell;
            var here = shell.ActiveTab!;

            var background = shell.Left.AddTab(At("photos", "2026"), activate: false);

            shell.ToggleSplit();
            Settle();
            var right = shell.Right!.ActiveTab!;
            await right.NavigateAsync(At("photos", "2026", "june"));

            shell.NewWindowCommand.Execute(null);
            Settle();
            var peer = Assert.Single(window.Services.Windows, w => !ReferenceEquals(w, window));
            var elsewhere = peer.Shell.ActiveTab!;
            await elsewhere.NavigateAsync(At("photos", "2026"));

            await Until(() => background.IsLoaded && right.IsLoaded && elsewhere.IsLoaded, "the tabs never loaded");

            Assert.False(await here.TryRenameAsync(await Row(here, "photos"), "pictures"), "a held folder was renamed");
            Assert.Equal("something inside that folder is open", here.Status);
            Assert.True(Directory.Exists(At("photos")) && !Directory.Exists(At("pictures")));
            Assert.True(holder.IsRunning, "the other program was closed");

            await Until(() => background.IsLoaded && !background.IsLoading && right.IsLoaded && !right.IsLoading
                              && elsewhere.IsLoaded && !elsewhere.IsLoading,
                "a pane the hold let go of was left loading or unloaded");

            Assert.True(PathRules.Same(background.CurrentPath, At("photos", "2026")));
            Assert.True(PathRules.Same(right.CurrentPath, At("photos", "2026", "june")));
            Assert.True(PathRules.Same(elsewhere.CurrentPath, At("photos", "2026")));

            await Watching(background, "the background tab is not watching after the refused rename");
            await Watching(right, "the split's right half is not watching after the refused rename");
            await Watching(elsewhere, "the other window's tab is not watching after the refused rename");
            await Watching(here, "the pane the rename was asked in is not watching");

            // And the same rename works once the other program lets go.
            holder.Dispose();

            Assert.True(await here.TryRenameAsync(await Row(here, "photos"), "pictures"), $"the rename was refused: {here.Status}");

            await Until(() => PathRules.Same(elsewhere.CurrentPath, At("pictures", "2026"))
                              && PathRules.Same(right.CurrentPath, At("pictures", "2026", "june"))
                              && elsewhere.IsLoaded && right.IsLoaded,
                $"the tabs did not follow: {elsewhere.CurrentPath}, {right.CurrentPath}");

            await Watching(right, "the split's right half is not watching its new place");
            await Watching(elsewhere, "the other window's tab is not watching its new place");
        }
        finally
        {
            holder.Dispose();
            CloseAll(window);
        }
    }

    // ---- a load in flight ------------------------------------------------------

    /// <summary>
    /// **A tab still reading a large subfolder when the rename is asked.**
    ///
    /// The tab's load opens its watch on the pool beside the enumeration, so
    /// it can be holding the subfolder at the moment of the rename. The rename
    /// must go through on the first press. The tab must end up at the new
    /// place, fully loaded and watching.
    /// </summary>
    [AvaloniaFact]
    public async Task Qa_a_tab_still_loading_a_big_subfolder_does_not_refuse_the_rename()
    {
        var big = At("photos", "big");
        Directory.CreateDirectory(big);

        for (var i = 0; i < 20_000; i++) File.WriteAllText(Path.Combine(big, $"f{i:00000}.txt"), "");

        var window = await OpenAsync();

        try
        {
            var shell = window.Shell;
            var here = shell.ActiveTab!;
            var photos = await Row(here, "photos");

            var loading = shell.Left.AddTab(big, activate: false);

            // Not waited for: the rename is asked while the tab reads.
            Settle();

            Assert.True(await here.TryRenameAsync(photos, "pictures"), $"the rename was refused: {here.Status}");

            var moved = At("pictures", "big");

            await Until(() => PathRules.Same(loading.CurrentPath, moved) && loading.IsLoaded && !loading.IsLoading
                              && loading.Entries.Count >= 20_000,
                $"the loading tab is at {loading.CurrentPath}, loaded {loading.IsLoaded}, {loading.Entries.Count} rows",
                seconds: 60);

            await Watching(loading, "the tab that was loading is not watching its new place");
        }
        finally
        {
            CloseAll(window);
        }
    }

    // ---- a never-loaded restored tab ---------------------------------------------

    /// <summary>
    /// **A tab restored from the session and never looked at holds nothing.**
    /// It must follow the rename, back stack included, and stay unloaded. When
    /// the tab is opened later, it loads and watches at the new place.
    /// </summary>
    [AvaloniaFact]
    public async Task Qa_a_restored_tab_nobody_has_opened_follows_and_loads_at_the_new_place_when_opened()
    {
        Directory.CreateDirectory(At("photos", "2026"));

        var window = await OpenAsync();

        try
        {
            var shell = window.Shell;
            var here = shell.ActiveTab!;

            var restored = shell.Left.AddRestoredTab(new TabState
            {
                Path = At("photos", "2026"),
                BackStack = [At("photos"), _root],
            });
            Settle();

            Assert.False(restored.IsLoaded);
            Assert.False(restored.IsLoading);

            Assert.True(await here.TryRenameAsync(await Row(here, "photos"), "pictures"), $"the rename was refused: {here.Status}");

            await Until(() => PathRules.Same(restored.CurrentPath, At("pictures", "2026")),
                $"the restored tab is at {restored.CurrentPath}");

            Assert.False(restored.IsLoaded, "following loaded a tab nobody had opened");
            Assert.Contains(restored.BackSteps, s => PathRules.Same(s.FullPath, At("pictures")));
            Assert.DoesNotContain(restored.BackSteps, s => PathRules.Same(s.FullPath, At("photos")));

            shell.Left.ActiveTab = restored;

            await Until(() => restored.IsLoaded && !restored.IsLoading && string.IsNullOrEmpty(restored.LoadError),
                "the restored tab did not load at its new place");
            await Watching(restored, "the restored tab is not watching its new place");
        }
        finally
        {
            CloseAll(window);
        }
    }

    // ---- git ---------------------------------------------------------------------

    private static void Git(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = folder,
        };

        foreach (var a in new[] { "-c", "user.name=qa", "-c", "user.email=qa@example.invalid", "-c", "commit.gpgsign=false" }.Concat(args))
            info.ArgumentList.Add(a);

        using var p = Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();

        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {err}");
    }

    private static bool GitIsHere()
    {
        try { return new GitVersionControl().IsAvailable; }
        catch (Exception) { return false; }
    }


    /// <summary>A repository at <paramref name="repo"/> with one committed file
    /// at src\deep\code.txt, modified since, and many untracked files so that
    /// git status takes a moment to walk.</summary>
    private static void MakeRepository(string repo)
    {
        Directory.CreateDirectory(Path.Combine(repo, "src", "deep"));
        File.WriteAllText(Path.Combine(repo, "src", "deep", "code.txt"), "one");

        for (var i = 0; i < 3000; i++) File.WriteAllText(Path.Combine(repo, "src", $"u{i:0000}.txt"), "");

        Git(repo, "init", "-q");
        Git(repo, "add", Path.Combine("src", "deep", "code.txt"));
        Git(repo, "commit", "-q", "-m", "one");
        File.WriteAllText(Path.Combine(repo, "src", "deep", "code.txt"), "two");
    }

    private static bool Marked(PaneViewModel pane, string file, VcsState state)
        => pane.VcsStates.Any(kv => kv.Value == state && PathRules.Same(kv.Key, file));

    /// <summary>
    /// **A repository renamed from another tab keeps its marks** (real-app
    /// scenario c, without the screen).
    ///
    /// The setup is a real repository with git decorations on and a tab inside
    /// it, which watches .git. The repository folder itself is renamed from the
    /// tab above it. The tab follows, and the modified file is marked Modified
    /// again under the new name, which only a fresh git status can give.
    /// </summary>
    [AvaloniaFact]
    public async Task Qa_a_real_repository_renamed_from_another_tab_gets_its_marks_back()
    {
        if (!GitIsHere()) return;

        var repo = At("repo");
        MakeRepository(repo);

        var window = await OpenAsync();
        PaneViewModel.Vcs = new GitVersionControl();

        try
        {
            var shell = window.Shell;
            var here = shell.ActiveTab!;

            var inside = shell.Left.AddTab(Path.Combine(repo, "src", "deep"), activate: false);

            await Until(() => inside.IsLoaded && Marked(inside, Path.Combine(repo, "src", "deep", "code.txt"), VcsState.Modified),
                "the tab inside never showed the modified file");

            await here.RefreshAsync();
            Assert.True(await here.TryRenameAsync(await Row(here, "repo"), "repo2"), $"the rename was refused: {here.Status}");

            var file = At("repo2", "src", "deep", "code.txt");

            await Until(() => PathRules.Same(inside.CurrentPath, At("repo2", "src", "deep")) && inside.IsLoaded
                              && Marked(inside, file, VcsState.Modified),
                $"the marks did not come back: {inside.CurrentPath}, "
                + string.Join(", ", inside.VcsStates.Select(kv => $"{kv.Key}={kv.Value}")), seconds: 40);

            await Watching(inside, "the tab inside the repository is not watching its new place");
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>
    /// **Git status above the folder** (review finding 17), with real git.
    ///
    /// The pane at the repository's root walks src with its status. The test
    /// renames src from that pane as soon as it has listed, while its status
    /// may still be running, and while a tab inside src watches. The rename
    /// must go through on the first press. The tab inside must follow. The
    /// pane above must ask git again, which now reports the renamed folder as
    /// untracked: git's own answer for a tracked folder renamed outside git.
    /// </summary>
    [AvaloniaFact]
    public async Task Qa_git_status_of_the_pane_above_does_not_refuse_the_rename_and_is_asked_again()
    {
        if (!GitIsHere()) return;

        var repo = At("repo");
        MakeRepository(repo);

        var window = await OpenAsync();
        PaneViewModel.Vcs = new GitVersionControl();

        try
        {
            var shell = window.Shell;
            var top = shell.ActiveTab!;

            var inside = shell.Left.AddTab(Path.Combine(repo, "src", "deep"), activate: false);
            await Until(() => inside.IsLoaded, "the tab inside never loaded");

            await top.NavigateAsync(repo);
            var src = await Row(top, "src");

            Assert.True(await top.TryRenameAsync(src, "lib"), $"the rename was refused: {top.Status}");

            await Until(() => PathRules.Same(inside.CurrentPath, Path.Combine(repo, "lib", "deep")) && inside.IsLoaded,
                $"the tab inside is at {inside.CurrentPath}");

            await Until(() => Marked(top, Path.Combine(repo, "lib"), VcsState.Untracked),
                "the pane above never asked git again: "
                + string.Join(", ", top.VcsStates.Select(kv => $"{kv.Key}={kv.Value}")), seconds: 40);

            await Watching(inside, "the tab inside is not watching its new place");
        }
        finally
        {
            CloseAll(window);
        }
    }

    // ---- batch rename, real engine -----------------------------------------------

    /// <summary>
    /// **A batch rename stopped by a held file is two undo steps, and both put
    /// back what they did.**
    ///
    /// On the real engine: the run renames a.txt, stops at b.txt (held by
    /// another program) and shows Try again. Once the other program lets go,
    /// Try again finishes b.txt and c.txt. Ctrl+Z twice then brings every
    /// original name back. Every name on disk is checked at each step.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task Qa_a_batch_stopped_by_a_held_file_is_finished_by_Try_again_and_undone_in_two_steps()
    {
        foreach (var n in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(At(n), n);

        var window = await OpenAsync();
        var holder = AnotherProgram.HoldingFile(At("b.txt"));

        try
        {
            var pane = window.Shell.ActiveTab!;
            var ops = window.Services.Platform.Operations;

            await pane.RefreshAsync();
            var entries = new[] { await Row(pane, "a.txt"), await Row(pane, "b.txt"), await Row(pane, "c.txt") };

            var model = new BatchRenameViewModel(entries,
                (entry, name) => pane.RenameOrThrowAsync(entry, name),
                pane.Entries,
                pane.BeginRenameGroup,
                () => BatchRenameViewModel.OnDisk(entries));

            var finished = false;
            model.Finished += (_, _) => finished = true;

            model.Pattern = "x ###";
            Settle();

            await model.ApplyCommand.ExecuteAsync(null);
            Settle();

            Assert.False(finished);
            Assert.True(model.IsInUse, $"no Try again after: {model.Summary}");
            Assert.True(File.Exists(At("x 001.txt")) && File.Exists(At("b.txt")) && File.Exists(At("c.txt")),
                "the run did not stop at the held file");

            // What landed before the stop is one step already.
            Assert.True(ops.CanUndo);

            holder.Dispose();

            await model.ApplyCommand.ExecuteAsync(null);
            Settle();

            Assert.True(finished, $"Try again did not finish the run: {model.Summary}");
            Assert.Equal(new[] { "x 001.txt", "x 002.txt", "x 003.txt" },
                Directory.GetFiles(_root, "*.txt").Select(Path.GetFileName).Order().ToArray());

            await ops.UndoAsync(CancellationToken.None);

            Assert.Equal(new[] { "b.txt", "c.txt", "x 001.txt" },
                Directory.GetFiles(_root, "*.txt").Select(Path.GetFileName).Order().ToArray());

            await ops.UndoAsync(CancellationToken.None);

            Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" },
                Directory.GetFiles(_root, "*.txt").Select(Path.GetFileName).Order().ToArray());

            Assert.Equal("a.txt", File.ReadAllText(At("a.txt")));
            Assert.Equal("b.txt", File.ReadAllText(At("b.txt")));
            Assert.Equal("c.txt", File.ReadAllText(At("c.txt")));
        }
        finally
        {
            holder.Dispose();
            CloseAll(window);
        }
    }

    // ---- cut, rename, paste -------------------------------------------------------

    private sealed class Clipboard : IClipboardService
    {
        public ClipboardPayload? Held { get; set; }

        public Task<bool> SetFilesAsync(ClipboardAction action, IReadOnlyList<string> paths)
        {
            Held = new ClipboardPayload(action, paths);
            return Task.FromResult(true);
        }

        public Task<ClipboardPayload?> GetFilesAsync() => Task.FromResult(Held);
        public Task<bool> HasFilesAsync() => Task.FromResult(Held is not null);
        public Task<bool> SetTextAsync(string text) => Task.FromResult(true);
    }

    private static void UseClipboard(object owner, IClipboardService clipboard)
        => owner.GetType()
            .GetField("_clipboard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(owner, clipboard);

    /// <summary>
    /// **Cut a file, rename the folder it is in, then paste.** The paste must
    /// move the file from where it now is, the renamed folder, into the
    /// destination. Nothing may be left at either name, and the paste must not
    /// say "not there".
    /// </summary>
    [AvaloniaFact]
    public async Task Qa_a_cut_then_a_rename_of_its_folder_then_paste_moves_the_file()
    {
        Directory.CreateDirectory(At("photos", "sub"));
        Directory.CreateDirectory(At("into"));
        File.WriteAllText(At("photos", "sub", "a.txt"), "payload");

        var window = await OpenAsync();
        var clipboard = new Clipboard();

        try
        {
            var shell = window.Shell;
            var here = shell.ActiveTab!;

            UseClipboard(shell, clipboard);

            var source = shell.Left.AddTab(At("photos", "sub"), activate: false);
            var target = shell.Left.AddTab(At("into"), activate: false);
            UseClipboard(source, clipboard);
            UseClipboard(target, clipboard);

            await Until(() => source.IsLoaded && target.IsLoaded, "the tabs never loaded");

            var cut = await Row(source, "a.txt");
            source.SelectedEntry = cut;
            source.SelectedEntries.Clear();
            source.SelectedEntries.Add(cut);
            await source.CutSelectionToClipboardAsync();
            Settle();

            Assert.Equal(ClipboardAction.Cut, clipboard.Held?.Action);

            Assert.True(await here.TryRenameAsync(await Row(here, "photos"), "pictures"), $"the rename was refused: {here.Status}");

            var moved = At("pictures", "sub", "a.txt");

            await Until(() => clipboard.Held?.Paths.Count == 1 && PathRules.Same(clipboard.Held.Paths[0], moved),
                $"the clipboard still names {string.Join(", ", clipboard.Held?.Paths ?? [])}");

            await target.PasteAsync();

            await Until(() => File.Exists(At("into", "a.txt")), $"the paste did not arrive: {target.Status}");

            Assert.Equal("payload", File.ReadAllText(At("into", "a.txt")));
            Assert.False(File.Exists(moved));
            Assert.False(File.Exists(At("photos", "sub", "a.txt")));
            Assert.DoesNotContain("not there", target.Status ?? "");
        }
        finally
        {
            CloseAll(window);
        }
    }

    // ---- names that start the same -----------------------------------------------

    /// <summary>
    /// **The prefix trap, both ways round.** "photos" is renamed to "photo",
    /// which is a prefix of a sibling, "photos2026". A tab in the sibling must
    /// neither move nor stop watching. The tab inside the renamed folder must
    /// follow it. Then "photo" is renamed to "Photo", a case-only rename,
    /// which Windows performs through a staging name. The tab must follow
    /// that too, and keep watching.
    /// </summary>
    [AvaloniaFact]
    public async Task Qa_a_rename_onto_a_prefix_of_a_sibling_and_a_case_only_rename_leave_the_right_tabs()
    {
        Directory.CreateDirectory(At("photos", "in"));
        Directory.CreateDirectory(At("photos2026", "in"));

        var window = await OpenAsync();

        try
        {
            var shell = window.Shell;
            var here = shell.ActiveTab!;

            var inside = shell.Left.AddTab(At("photos", "in"), activate: false);
            var sibling = shell.Left.AddTab(At("photos2026", "in"), activate: false);
            await Until(() => inside.IsLoaded && sibling.IsLoaded, "the tabs never loaded");

            Assert.True(await here.TryRenameAsync(await Row(here, "photos"), "photo"), $"the rename was refused: {here.Status}");

            await Until(() => PathRules.Same(inside.CurrentPath, At("photo", "in")) && inside.IsLoaded,
                $"the tab inside is at {inside.CurrentPath}");

            Assert.Equal(At("photos2026", "in"), sibling.CurrentPath);
            await Watching(sibling, "the sibling's tab stopped watching");

            Assert.True(await here.TryRenameAsync(await Row(here, "photo"), "Photo"), $"the case-only rename was refused: {here.Status}");

            await Until(() => string.Equals(inside.CurrentPath, At("Photo", "in"), StringComparison.Ordinal) && inside.IsLoaded,
                $"the tab inside is at {inside.CurrentPath} after the case-only rename");

            Assert.True(Directory.Exists(At("Photo")));
            Assert.Equal("Photo", new DirectoryInfo(_root).GetDirectories("photo*", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .Select(d => d.Name).Single(n => !n.StartsWith("photos", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(At("photos2026", "in"), sibling.CurrentPath);

            await Watching(inside, "the tab inside stopped watching after the case-only rename");
        }
        finally
        {
            CloseAll(window);
        }
    }
}
