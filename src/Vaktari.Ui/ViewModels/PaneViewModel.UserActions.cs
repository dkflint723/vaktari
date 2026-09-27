using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The two things a pane runs on the user's behalf: the scripts in their
/// scripts folder, and the templates behind "New from template".
/// </summary>
public sealed partial class PaneViewModel
{
    // ---- user scripts --------------------------------------------------

    public ObservableCollection<ScriptCommand> Scripts { get; } = new();

    public bool HasScripts => Scripts.Count > 0;

    /// <summary>
    /// The item menu's Scripts, which run on the selection — in a real folder.
    ///
    /// **Not in the bin**, where a row's path is where the item used to be: a
    /// script handed it acts on whatever lives there now, the fault every
    /// other selection verb in that listing already refuses.
    ///
    /// **Nor in a search, Recent, This PC or a scan**, where the row was
    /// offered and the script could not start: a script runs with the folder
    /// on screen as its working directory, and there the folder is
    /// "vaktari:search:…", which both runners hand to the process as its
    /// WorkingDirectory — and the process fails to start. Refused in
    /// <see cref="RunAsync"/> as well, for the same reason.
    /// </summary>
    public bool CanRunScriptsOnSelection => HasScripts && CanActOnSelection && IsRealFolder;

    /// <summary>
    /// The background menu's Scripts, which run in the folder with nothing
    /// selected — shown whenever there is a folder and somewhere for scripts
    /// to live, whether or not any are there yet, because its last row is how
    /// you go and put one there.
    ///
    /// **This replaced a row that stood in for an empty submenu** — "Add your
    /// own scripts", on both a row's menu and the background's. The row said
    /// what the feature was for and opened the folder; the submenu now does
    /// both, and a script added there appears in it on the next opening.
    /// </summary>
    public bool CanRunScriptsHere => _scripts is not null && IsRealFolder;

    /// <summary>
    /// The background menu's Scripts submenu: every script, then a rule and
    /// the row that opens the folder they live in.
    ///
    /// **One collection rather than a submenu with a fixed row under a bound
    /// one**, because Avalonia's MenuItem takes Items or ItemsSource, never
    /// both. The rule is a real Separator control, which an ItemsSource uses as
    /// its own container — the shape ShellMenuItems already has. Rebuilt from
    /// <see cref="Scripts"/> each time the menu opens, so the two cannot
    /// disagree — see <see cref="RefreshScriptRows"/>.
    /// </summary>
    public ObservableCollection<object> ScriptRows { get; } = new();

    [RelayCommand]
    public void OpenScriptsFolder()
    {
        if (_scripts is null) return;

        // **Made if it is missing.** Both runners create it once, when they are
        // built; a folder deleted since would have been handed to the launcher
        // as a path that is not there.
        // The row is how somebody starts using the feature, so it must always
        // land somewhere.
        try
        {
            Directory.CreateDirectory(_scripts.ScriptsDirectory);
        }
        catch (Exception ex)
        {
            Status = Failures.Describe(ex, "make the scripts folder");
            return;
        }

        _launcher?.Open(_scripts.ScriptsDirectory);
    }

    /// <summary>
    /// A row of the background menu's Scripts submenu: a script, run in the
    /// folder with NO selection, or the row that opens the scripts folder.
    ///
    /// **Not RunScript, which hands the script the selection.** A right-click
    /// on empty space keeps the selection, so the same command from the
    /// background menu would have run the script on files the menu is not
    /// about. One command for both kinds of row because one anchored style
    /// sets it on every row of the submenu.
    /// </summary>
    [RelayCommand]
    public async Task RunScriptHereAsync(object? row)
    {
        if (row is ScriptsFolderRow)
        {
            OpenScriptsFolder();
            return;
        }

        if (row is ScriptCommand script) await RunAsync(script, []).ConfigureAwait(false);
    }

    [RelayCommand]
    public Task RunScriptAsync(ScriptCommand? script)
        => script is null ? Task.CompletedTask : RunAsync(script, SelectionPaths());

    private async Task RunAsync(ScriptCommand script, IReadOnlyList<string> selection)
    {
        if (_scripts is null) return;

        // The working directory is the folder on screen; a listing that is not
        // one has no directory to start in. See CanRunScriptsOnSelection.
        if (!IsRealFolder)
        {
            Status = $"{script.Name} runs in a folder — open one first";
            return;
        }

        Status = $"running {script.Name}…";

        try
        {
            var output = await _scripts
                .RunAsync(script, CurrentPath, selection, CancellationToken.None)
                .ConfigureAwait(false);

            // The watcher picks up whatever the script changed on disk, so the
            // listing does not need refreshing here.
            await Dispatcher.UIThread.InvokeAsync(() => Status = output);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"{script.Name}: {ex.Message}");
        }
    }

    public void RefreshScripts()
    {
        Scripts.Clear();
        if (_scripts is null) return;

        foreach (var script in _scripts.Discover()) Scripts.Add(script);
        OnPropertyChanged(nameof(HasScripts));
        OnPropertyChanged(nameof(CanRunScriptsOnSelection));
    }

    /// <summary>
    /// Rebuilds <see cref="ScriptRows"/> from <see cref="Scripts"/>, for the
    /// background menu that is about to show them.
    ///
    /// **Not part of RefreshScripts, which the constructor calls.** The rule
    /// in this list is a real Separator — an Avalonia control, with the UI
    /// thread's affinity — and a pane is constructed off that thread by every
    /// plain [Fact] that makes one, and by nothing that promises otherwise.
    /// ShellMenuItems makes its rules the same way for the same reason: only
    /// when the menu asks, which is on the UI thread. So these rows are built
    /// where the menu is, as it opens — PrepareListingMenu calls this.
    /// </summary>
    public void RefreshScriptRows()
    {
        ScriptRows.Clear();

        foreach (var script in Scripts) ScriptRows.Add(script);

        // The rule only between two things: with no scripts yet the submenu is
        // the folder row alone.
        if (Scripts.Count > 0) ScriptRows.Add(new Avalonia.Controls.Separator());

        ScriptRows.Add(ScriptsFolderRow.Instance);
    }

    /// <summary>
    /// Copy alongside. The operations layer already resolves a name collision
    /// by keeping both, which is exactly what duplicating means — so this is a
    /// copy whose destination is where the files already are.
    /// </summary>
    // ---- templates -------------------------------------------------------

    public ObservableCollection<FileTemplate> Templates { get; } = new();

    public bool HasTemplates => Templates.Count > 0;

    [RelayCommand]
    public async Task NewFromTemplateAsync(FileTemplate? template)
    {
        if (RefusedVirtualDestination(CurrentPath)) return;

        if (template is null || _ops is null) return;

        try
        {
            // A copy, then straight into rename — the name is the only thing
            // the user actually wants to decide.
            //
            // **A seed file is named by whoever installed it.** Measured on
            // Windows 11 26200: the Access row's ShellNew key points at
            // ACCESS12.ACC, so taking the leaf from the seed made "New >
            // Microsoft Access Database" produce ACCESS12.ACC — the wrong name
            // and the wrong extension. Leaf is what the row says the file
            // should be called; null means the template's own leaf is the
            // answer, which is every Linux one, because the user named it.
            //
            // **A dotfile is a name, not a bare extension.** Splitting on the
            // last dot made a second .gitignore into " 2.gitignore", with
            // nothing at all in front of the space. PathRules.SplitLeaf is
            // where that answer already lives — the copy engine and the trash
            // both ask it, and this was the third caller still guessing.
            var leaf = template.Leaf ?? Path.GetFileName(template.Path);
            var (stem, extension) = PathRules.SplitLeaf(leaf, isDirectory: false);

            var unique = NewItemName.Free(CurrentPath, stem, extension);

            // **A Windows template need not be a file at all.** Explorer's New
            // menu is the ShellNew registry keys, and the one row Windows
            // itself ships there carries its bytes inline — .zip's 22-byte
            // end-of-central-directory record, which no file on the machine
            // holds. Content is those bytes; null means the template really is
            // a file on disk, which is every Linux one and the five Office rows
            // measured here.
            await Task.Run(() => Fill(template, unique)).ConfigureAwait(true);

            // Undoable, the same way new folder and new file are: into the bin.
            _ops.RecordCreation(unique);

            await RefreshAsync().ConfigureAwait(true);

            BeginRenameOf(unique);
        }
        catch (Exception ex)
        {
            // The same sentence new file and new folder give. A template
            // deleted out from under the menu is the common case, and "that
            // file is not there any more" says it; System.IO makes the reader
            // parse a path to learn the same thing.
            Status = Failures.Describe(ex, "make that file");
        }
    }

    private static void Fill(FileTemplate template, string destination)
    {
        if (template.Content is { } bytes) { File.WriteAllBytes(destination, bytes); return; }

        File.Copy(template.Path, destination);
    }

    /// <summary>Re-read on every menu open: a template is a file the user drops
    /// into a folder, and needing a restart to see it would be baffling. On
    /// Windows the answer behind this is cached — see WindowsTemplates, where a
    /// ShellNew key changes when software is installed rather than when a file
    /// appears in a folder.</summary>
    public void RefreshTemplates()
    {
        Templates.Clear();
        if (_templates is null) return;

        foreach (var template in _templates.Discover()) Templates.Add(template);
        OnPropertyChanged(nameof(HasTemplates));
    }
}

/// <summary>
/// The last row of the background menu's Scripts submenu, which opens the
/// folder the scripts live in.
///
/// **A record of its own rather than a ScriptCommand standing in for one**, so
/// that <see cref="PaneViewModel.RunScriptHereAsync"/> can tell the two apart
/// by type and nothing can ever try to execute the folder. One instance: it
/// carries no state.
/// </summary>
public sealed record ScriptsFolderRow(string Label)
{
    public static ScriptsFolderRow Instance { get; } = new("Open scripts folder");
}
