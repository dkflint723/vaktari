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
            var target = Path.Combine(CurrentPath, Path.GetFileName(template.Path));
            var unique = target;
            var counter = 2;

            while (File.Exists(unique) || Directory.Exists(unique))
            {
                unique = Path.Combine(CurrentPath,
                    $"{Path.GetFileNameWithoutExtension(target)} {counter++}{Path.GetExtension(target)}");
            }

            await Task.Run(() => File.Copy(template.Path, unique)).ConfigureAwait(true);

            // Undoable, the same way new folder and new file are: into the bin.
            _ops.RecordCreation(unique);

            await RefreshAsync().ConfigureAwait(true);

            BeginRenameOf(unique);
        }
        catch (Exception ex)
        {
            Status = $"could not create from template: {ex.Message}";
        }
    }

    /// <summary>Re-read on every menu open: a template is a file the user drops
    /// into a folder, and needing a restart to see it would be baffling.</summary>
    public void RefreshTemplates()
    {
        Templates.Clear();
        if (_templates is null) return;

        foreach (var template in _templates.Discover()) Templates.Add(template);
        OnPropertyChanged(nameof(HasTemplates));
    }
}
