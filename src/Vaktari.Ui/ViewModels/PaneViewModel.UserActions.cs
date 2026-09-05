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

    [RelayCommand]
    public void OpenScriptsFolder()
    {
        if (_scripts is not null) _launcher?.Open(_scripts.ScriptsDirectory);
    }

    [RelayCommand]
    public async Task RunScriptAsync(ScriptCommand? script)
    {
        if (_scripts is null || script is null) return;

        Status = $"running {script.Name}…";

        try
        {
            var output = await _scripts
                .RunAsync(script, CurrentPath, SelectionPaths(), CancellationToken.None)
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
