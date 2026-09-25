using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Vaktari.Core;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// The settings dialog, and what a save reaches when it closes.
///
/// **What makes this big is not that it opens a window.** Every other dialog
/// this class puts up is a one-line lambda in the constructor. This one is
/// long because the window it opens asks seven questions back — browse for a
/// startup folder, run diagnostics, export, import, install an icon theme,
/// browse for one, open a URL — and because its Closed handler is where a
/// saved setting becomes a running application, through a dozen separate
/// statements.
///
/// SettingsChangedEverywhere is one of those statements, lifted out because it
/// is the one that leaves this window: it tells the OTHER windows in the
/// family. It comes here rather than staying behind because its only caller in
/// the application is that Closed handler. It is <c>internal</c> rather than
/// private only so a test can call it by reference, which is a thing worth
/// saying out loud — a member that looks like it has outside callers, and has
/// none except a test, is exactly how a member gets left somewhere nothing
/// calls it.
///
/// **Three fields come too, for the same reason and it is the same fault.**
/// _platform, _defaultFileManager and _fileManager are read in exactly two
/// places, both inside ShowSettings. Left behind they would have sat in
/// MainWindow.axaml.cs assigned and never read — no check would have noticed,
/// because the member set is unchanged and every suite stays green. Their
/// ASSIGNMENTS stay in the constructor, which is how every other field this
/// class declares in a sibling file already works.
///
/// What this file calls back across the boundary, so a reader knows the edge
/// is real and deliberate: _services and _shell throughout, Suggested for the
/// three folder pickers, and one call each to ApplyScales,
/// CheckForUpdatesAsync and RefreshTitle, plus _theme, _launcher and
/// _fullPathInTitle. That is the shape of a save: it touches nearly everything
/// the window owns, which is the argument for the save being in one place
/// rather than for that place being anywhere in particular.
///
/// **Suggested deliberately stays behind.** Three of its four call sites are
/// here, but its own comment records that it stopped being a local function
/// BECAUSE the transfer picker asks the same question from outside settings —
/// so a helper whose stated reason to exist is "used from outside settings" is
/// the last thing that should end up inside a settings file.
/// </summary>
public partial class MainWindow
{
    private readonly IPlatform _platform;
    private readonly IDefaultFileManager? _defaultFileManager;
    private readonly IFileManagerService? _fileManager;
    /// <summary>
    /// A save, applied to every window in the family rather than to the one the
    /// dialog was opened from.
    ///
    /// **A save reached only its own window, and the interface size is what
    /// made that visible.** The application dictionary this writes is shared —
    /// so a second window's sidebar, toolbar, tab strip and status bar took the
    /// new size the moment it was written — while each pane keeps its OWN
    /// dictionary, which shadows the application's and is rewritten only when
    /// that pane is told to. So the peer window drew 28px chrome around 14px
    /// listings, and its column thresholds went on measuring against a text
    /// size that had been replaced. Before this setting existed a save moved
    /// only spacing, and the two halves of a window could not visibly disagree
    /// about type size.
    ///
    /// Every window, not every SHELL: the pane dictionaries hang off controls,
    /// and it is <see cref="ShellViewModel.OnSettingsChanged"/> that makes each
    /// pane rewrite its own.
    ///
    /// **"Show full path in title bar" reached only the window the dialog was
    /// opened from.** Each window keeps its own copy of the flag, and the save
    /// handler set that copy on <c>this</c> alone — so every peer window went
    /// on titling itself the old way, on every navigation, until a restart.
    /// Set here, from the settings just applied, because this loop is the one
    /// place that already visits every window in the family.
    /// </summary>
    internal void SettingsChangedEverywhere()
    {
        var fullPath = AppSettings.Current.Startup.ShowFullPathInTitleBar;

        // Over a copy: OnSettingsChanged reaches most of a window, and the
        // family list is the one every window adds itself to and removes itself
        // from — iterating it live would be trusting that none of that runs.
        foreach (var window in _services.Windows.ToList())
        {
            window.Shell.OnSettingsChanged();

            window._fullPathInTitle = fullPath;
            window.RefreshTitle();
        }
    }
    /// <summary>
    /// Saving swaps AppSettings.Current and writes the file. Most of what the
    /// Startup page controls only means anything at launch, so it is applied
    /// then rather than re-run here — except the title bar, which is visible
    /// right now and would otherwise look broken until a restart.
    /// </summary>
    private void ShowSettings()
    {
        var model = new SettingsViewModel(
            AppSettings.Current, _defaultFileManager, _platform.FileIcons, _fileManager,
            // From the store rather than rebuilt from the same two pieces: the
            // dialog now shows this path and writes copies of that file, and a
            // second spelling of where it is would be wrong in exactly the
            // cases that matter — a portable install, a test directory.
            _services.SettingsStore.FilePath,
            _services.FolderViews,
            _services.Recents,
            _services.Searches,
            _services.SettingsStore.ReadOnlyReason,
            _services.UpdateAvailable?.Version);

        // The pane already holds the detected list, ordered and cached, so the
        // dialog borrows it rather than probing the disk again as it opens.
        if (_shell.ActiveTab is { } pane) model.UseTerminals(pane.Terminals);

        var window = new SettingsWindow(model);

        // The dialogs belong to the window. A view model that opens a folder
        // picker cannot be constructed in a test, and this one already is.
        model.StartupFolderBrowseRequested += async (_, _) =>
        {
            // Starting at whatever is already typed, when that is somewhere:
            // correcting a path is more common than choosing one from scratch.
            var start = await Suggested(window, model.StartupFolder.Trim());

            var picked = await window.StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Choose the folder Vaktari opens in",
                    AllowMultiple = false,
                    SuggestedStartLocation = start,
                });

            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;

            model.StartupFolder = folder;
        };

        model.DiagnosticsRequested += async (_, text) =>
        {
            try
            {
                if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
                model.SettingsFileStatus = "diagnostics copied — paste them into a bug report";
            }
            catch (Exception ex)
            {
                model.SettingsFileStatus = $"could not reach the clipboard: {ex.Message}";
            }
        };

        model.SettingsExportRequested += async (_, _) =>
        {
            var target = await window.StorageProvider.SaveFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "Save a copy of these settings",
                    SuggestedFileName = "vaktari-settings.json",
                    DefaultExtension = "json",
                    FileTypeChoices =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("Vaktari settings")
                        {
                            Patterns = ["*.json"],
                        },
                    ],
                });

            if (target?.TryGetLocalPath() is not { } path) return;

            model.ExportTo(path);
        };

        model.SettingsImportRequested += async (_, _) =>
        {
            // Starting where the real one lives, since a copy of it is most
            // often kept beside it.
            var start = await Suggested(
                window, Path.GetDirectoryName(_services.SettingsStore.FilePath) ?? "");

            var picked = await window.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Replace these settings from a copy",
                    AllowMultiple = false,
                    SuggestedStartLocation = start,
                    FileTypeFilter =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("Vaktari settings")
                        {
                            Patterns = ["*.json"],
                        },
                    ],
                });

            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } file) return;

            // Closes the dialog on success, and the Closed handler below then
            // applies and saves the imported state exactly as it applies a
            // Save — so an import lands everywhere a normal save lands, with no
            // second path to keep in step.
            model.ImportFrom(file);
        };

        // A file somebody downloaded themselves, unpacked exactly as a fetched
        // one is — same containment, same whitelist, same size caps.
        model.IconThemeArchiveRequested += async (_, _) =>
        {
            var picked = await window.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Choose an icon theme archive",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("Icon theme archive")
                        {
                            // The format is read from the file's first bytes, so
                            // these only decide what the dialog shows.
                            Patterns = ["*.tar.gz", "*.tar.xz", "*.tgz", "*.txz", "*.zip"],
                        },
                    ],
                });

            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } archive) return;

            await model.InstallIconThemeFromAsync(archive);
        };

        model.IconThemeBrowseRequested += async (_, _) =>
        {
            // Starting where the fetched themes are, since that is where most
            // of them will be. Null when nothing has been installed, which
            // leaves the picker wherever it would otherwise open.
            var start = await Suggested(window, Vaktari.Core.FileSystem.IconThemeCatalogue.InstallRoot);

            var picked = await window.StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Choose an icon theme folder",
                    AllowMultiple = false,
                    SuggestedStartLocation = start,
                });

            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;

            // **Checked now, not when the icons fail to change.** The usual
            // mistake is picking the folder the archive was extracted INTO
            // rather than the theme inside it, and the difference is invisible
            // until nothing happens.
            //
            // Off the UI thread, and said out loud while it happens. A theme
            // nobody has read before is never cached — that is what makes it a
            // new choice — so this is the one check that always pays the full
            // 2.8–3.1 seconds, and it used to pay it with the dialog frozen.
            // The read leaves a cache behind, so the launch after this one
            // opens with the icons already right.
            model.IconThemeStatus = "Reading that theme…";

            var read = await Task.Run(
                () => Vaktari.Core.FileSystem.FreedesktopIconTheme.FromFolder(path));

            model.IconThemeStatus = "";

            if (read is null)
            {
                model.IconThemeFolder = "";
                var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

                // Two causes, and they need different answers.
                //
                // The first names the symlink case, which is invisible
                // otherwise: the folder looks complete in a listing and simply
                // produces nothing. A variant extracted beside the theme it is
                // built from now falls back to it and never reaches here, so
                // what is left is a variant extracted on its own — and the
                // answer to that is the missing half, not a different theme.
                model.IconThemeProblem = File.Exists(Path.Combine(path, "index.theme"))
                    ? $"'{leaf}' has an index.theme but no icons Vaktari can read. Themes like "
                      + "this one keep most of their icons as links to the theme they are based "
                      + "on, and Windows drops those links when the archive is extracted. "
                      + "Extract the whole archive so the theme it is based on sits beside it, "
                      + "and Vaktari will use both."
                    : $"'{leaf}' has no index.theme in it. Choose the folder that came out of "
                      + "the archive — for Papirus that is the one called Papirus, not the "
                      + "folder you extracted it into.";
                return;
            }

            model.IconThemeProblem = "";
            model.IconThemeFolder = path;
        };

        // A web address or a folder: Open takes both, and the desktop decides
        // which of its own applications answers.
        model.OpenUrlRequested += (_, url) => _launcher?.Open(url);

        window.Closed += (_, _) =>
        {
            if (!model.Saved) return;

            AppSettings.Apply(model.Result);

            // The mapping follows the setting immediately, or a corrected
            // folder would need a restart to matter. Clearing it falls back
            // to the guess, the same as startup.
            _services.DriveLinks.LocalRoot =
                model.Result.General.ProtonDriveFolder is { Length: > 0 } chosen
                    ? chosen
                    : Vaktari.Core.Sharing.ProtonDriveLinks.GuessLocalRoot() ?? "";

            // Rebuilt on save, or choosing a theme would need a restart — and
            // the resolved-path cache has no theme in its key, so it has to be
            // dropped or it keeps serving files from the theme just abandoned.
            WindowServices.InstallIconTheme(_platform);
            Thumbnails.IconLoader.Invalidate();
            _services.SettingsStore.Save(model.Result);

            // Turned on just now: ask now rather than tomorrow. The check's
            // own cadence keeps a save that leaves it on from asking twice.
            if (model.Result.General.CheckForUpdates) _ = CheckForUpdatesAsync();

            // Here rather than in the dialog, so it lands through the one
            // handler that already applies a save — and so Cancel throws it
            // away like every other change made in that dialog.
            if (model.ForgetViewsOnSave) _services.FolderViews.ForgetAll();
            if (model.ForgetRecentOnSave) _services.Recents.ForgetAll();
            if (model.ForgetSearchHistoryOnSave) _services.Searches.ForgetAll();

            // The font lives in the theme resources, and ThemeApplier is the
            // only thing that writes them — so a saved font does nothing until
            // this runs. It was called at startup and on a Plasma scheme change
            // and nowhere else, which is why changing the font appeared to do
            // nothing at all.
            ThemeApplier.Apply(this, _theme?.Read());

            // Icon spacing lands in the SAME kind of place — a resource that
            // only the markup reads — so it needs the same treatment. Without
            // this the setting saves, the file records it, and absolutely
            // nothing moves until the next restart, which is precisely how the
            // font setting managed to look broken for weeks.
            ApplyScales(_shell.FontScale, _shell.IconScale);

            // Most settings are read at the moment they matter. Sorting and the
            // status bar are not — a listing already on screen was ordered under
            // the old rule, and a visibility binding needs telling. The title
            // bar's full-path choice goes with it, to every window.
            SettingsChangedEverywhere();
        };

        window.ShowDialog(this);
    }
}
