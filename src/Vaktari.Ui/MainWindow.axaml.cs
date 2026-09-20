using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core;
using Vaktari.Core.Places;
using Vaktari.Core.Search;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
#if VAKTARI_LINUX
using Vaktari.Linux;
#elif VAKTARI_WINDOWS
using Vaktari.Windows;
#endif
using Vaktari.Ui.Session;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;

    /// <summary>
    /// Everything this window shares with every other one — the platform, the
    /// four stores and the trash sweep. Built by the FIRST window and lent to
    /// every window it opens; <see cref="WindowServices"/> records why it is
    /// lent rather than reached for.
    /// </summary>
    private readonly WindowServices _services;

    /// <summary>The shell, for the family: <see cref="WindowServices.Compose"/>
    /// asks every live window for its own entry in the session.</summary>
    internal ShellViewModel Shell => _shell;

    internal WindowServices Services => _services;

    /// <summary>
    /// The once-a-day question to the releases page, when the setting asks
    /// for it — from App once the founder exists, and from a settings save
    /// that turns it on.
    ///
    /// **Off means nothing is asked**, not asked and discarded: the request
    /// is the thing the setting is about. The cadence and the silences are
    /// the check's own (see ReleaseCheck). The answer goes on the operation
    /// bar, the one line that stays until dismissed, and onto the settings
    /// footer's version line, where "What is new" is the link.
    /// </summary>
    /// <param name="running">The version to compare against; the build's
    /// own unless a test says otherwise, since a test host reports 0.0.0
    /// and a development build never asks.</param>
    internal async Task CheckForUpdatesAsync(string? running = null)
    {
        if (!AppSettings.Current.General.CheckForUpdates) return;

        var found = await Task
            .Run(() => _services.Updates.CheckAsync(running ?? Program.Version, CancellationToken.None))
            .ConfigureAwait(true);

        if (found is null) return;

        _services.UpdateAvailable = found;

        Shell.OperationStatus =
            $"Vaktari {found.Version} is available — Settings ▸ Vaktari {Program.Version} ▸ What is new";
    }

    private readonly Vaktari.Core.FileSystem.IApplicationLauncher? _launcher;
    private readonly IPlatform _platform;

    // Preferences, as distinct from the session. Read before it, because the
    // startup setting decides whether the session is consulted at all.
    private readonly SettingsState _settings;

    private readonly IDefaultFileManager? _defaultFileManager;
    private readonly IFileManagerService? _fileManager;
    private readonly IPropertiesProvider _properties;
    private readonly IThemeProvider? _theme;
    private readonly IAccessEditor? _accessEditor;

    /// <summary>
    /// Held so it can be let go again. The theme provider belongs to the
    /// SHARED platform and outlives this window, so a closed window that stayed
    /// subscribed went on re-running ThemeApplier against itself and calling
    /// the global Thumbnails.IconLoader.Invalidate() — once per dead window, on
    /// every desktop scheme change.
    /// </summary>
    private readonly EventHandler? _onThemeChanged;

    /// <summary>
    /// Held for the same reason as the one above. IconLoader's event is STATIC,
    /// so a closed window that stayed subscribed would be kept alive by it and
    /// would re-list its dead panes on every icon swap.
    /// </summary>
    private readonly EventHandler _onIconSourceChanged;

    /// <summary>
    /// The founder. Kept parameterless and delegating so App.axaml.cs, and the
    /// tests that build a window, read exactly as they did — and so
    /// TabReorderTests, which scans the body of `public MainWindow()`, still
    /// finds the pointer-release wiring in the constructor immediately below.
    /// That test's anchor should be updated if these two are ever separated,
    /// rather than the constructors rearranged to keep it happy.
    /// </summary>
    public MainWindow() : this(shared: null, restoreIndex: 0, openAt: null, seed: null, like: null) { }

    /// <summary>
    /// One window.
    ///
    /// <paramref name="shared"/> null means "you are the first" — build the
    /// application's shared half and own it. Every other window is handed the
    /// founder's, because two of any of those objects on one state directory is
    /// two writers of the same files.
    ///
    /// <paramref name="restoreIndex"/> is which saved window this one is, or
    /// negative for a window that is not being restored from the session at
    /// all. <paramref name="openAt"/> is the folder it was ASKED for, and
    /// <paramref name="seed"/> the view it should arrive in.
    /// </summary>
    private MainWindow(
        WindowServices? shared, int restoreIndex, string? openAt,
        WindowSession? seed, PaneViewModel? like)
    {
        InitializeComponent();
        AppIcon.Apply(this);

        var founder = shared is null;

        _services = shared ?? WindowServices.Create();

        var platform = _services.Platform;

        // Per-window handles onto shared objects. These were assigned from the
        // block that has moved to WindowServices.Create; they stay here because
        // they are fields of a window, not things an application owns one of.
        _defaultFileManager = platform.DefaultFileManager;
        _properties = platform.Properties;
        _accessEditor = platform.AccessEditor;
        _launcher = platform.Launcher;
        _platform = platform;
        _virtualDrop = platform.VirtualFileDrop;
        _shortcuts = platform.Shortcuts;
        _settings = _services.Settings;

        // Applied before anything else paints, and re-applied whenever Plasma's
        // scheme changes, so the window follows the desktop live.
        _theme = platform.Theme;
        var platformIcons = platform.Icons;
        ThemeApplier.Apply(this, _theme?.Read());

        if (_theme is not null)
        {
            // **Named rather than anonymous, and that is the whole of the
            // difference.** Until windows could close, a subscription for the
            // life of the process was the life of the window. Now a closed
            // window that is still subscribed keeps its visual tree alive AND
            // goes on reacting, so OnClosed needs something to hand back.
            _onThemeChanged = (_, _) => Dispatcher.UIThread.Post(() =>
            {
                // Plasma rewrites kdeglobals in pieces; a short settle avoids
                // reading it mid-write and picking up half a scheme.
                Task.Delay(150).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
                {
                    var palette = _theme.Read();

                    ThemeApplier.Apply(this, palette);

                    // **The desktop's text size arrives on that same palette**,
                    // and every metric in the window is computed from it — so
                    // the scheme change that carries a new one has to re-run
                    // them. Without this, raising Plasma's general font or the
                    // Windows text-size slider repainted the colours and left
                    // the window at the size it started at until the next
                    // settings save, which is indistinguishable from the
                    // setting not being read at all.
                    RescaleForDesktop();

                    // The palette carries the desktop's single-click setting
                    // too, and Apply has just rewritten the static holding it.
                    // A listing already on screen binds to that decision, so
                    // without this the rows keep the pointer they were built
                    // with until the pane is rebuilt — the same trap the
                    // selection boxes fell into on a settings save, and the
                    // same reason RescaleForDesktop is here.
                    //
                    // Through the property rather than the field: this lambda is
                    // built in the constructor, before the field it would read is
                    // assigned, and the compiler will not take "it only runs
                    // later" for an answer (CS8602).
                    Shell.RefreshActivation();

                    // Icons follow the desktop too: the resolved paths belong to
                    // the old icon theme and every cached drawable has the old
                    // text colour baked into its currentColor.
                    platformIcons?.Reload(palette?.IconTheme);
                    Thumbnails.IconLoader.Invalidate();
                }));
            });

            _theme.Changed += _onThemeChanged;
        }

        // Outside the theme block on purpose: an icon source also moves when a
        // THEME is chosen or cleared, which has nothing to do with the
        // desktop's palette and happens on machines with no theme provider at
        // all. Posted, because the announcement can arrive from the dispatcher
        // callback of a background build.
        _onIconSourceChanged = (_, _) => Dispatcher.UIThread.Post(() => Shell.RefreshPaneListings());

        Thumbnails.IconLoader.SourceChanged += _onIconSourceChanged;

        // **A first run got a settings file and nothing else.** The one line
        // that stays until dismissed says where the tour is; the tour itself
        // is on the view menu for anybody, any time, which is why this is a
        // line rather than a dialog — a file manager that opens on a dialog
        // has not opened. Founder only: a second window of a first run is not
        // a second first run.
        //
        // FIRST of the three startup lines, deliberately: it is the quietest,
        // so anything louder said below — a crash, a file this build will not
        // write — is posted after it and wins the one slot.
        if (founder && _services.FirstRun)
            Dispatcher.UIThread.Post(() => Shell.OperationStatus =
                "Welcome to Vaktari — the tour is under View options (≡) ▸ Take the tour");

        // **The previous run ended in a crash, and until now nothing said
        // so.** The marker is taken exactly once, so only the first window of
        // the next run says it; the operation bar is the one line that stays
        // until dismissed, which a status line does not.
        //
        // Posted, for the reason the theme handler above is: this runs in the
        // constructor before _shell is assigned, and the compiler will not
        // take "it only runs later" for an answer — measured, as a null
        // reference on the first test that built a window after a crash.
        if (Vaktari.Core.Diagnostics.Log.TakeCrashMarker() is { } crashed)
        {
            Vaktari.Core.Diagnostics.Log.Warn("startup", "previous run ended unexpectedly: " + crashed.Split('\n')[0]);

            Dispatcher.UIThread.Post(() => Shell.OperationStatus =
                "Vaktari closed unexpectedly last time — Settings ▸ Settings file ▸ Copy diagnostics");
        }

        // The same line for a settings file this build must not write: the
        // decision was made when the file was read, and this is where it is
        // said. Posted for the same reason as the crash notice above.
        if (_services.SettingsStore.ReadOnlyReason is { } readOnly)
            Dispatcher.UIThread.Post(() => Shell.OperationStatus = readOnly);

        // Not platform-specific: the clipboard comes from the toolkit.
        IClipboardService clipboard = ClipboardService.ForWindow(this);

        // Loaded synchronously so geometry is applied before first paint. An
        // async load would restore size and position after the window is
        // already on screen — a visible jump on every launch.
        //
        // Read ONCE, by the founder, and kept: a window restored out of it a
        // moment later must see the same state rather than re-read a file the
        // founder may already have written over.
        if (founder) _services.Restored = _services.Session.Load();

        var state = _services.Restored;

        ApplyGeometry(state, restoreIndex);

        _shell = new ShellViewModel(
            platform.FileSystem, platform.Operations, _services.Session,
            platform.Places, platform.Launcher, clipboard,
            platform.Scripts, platform.Templates, platform.Sharing)
        {
            GeometryProvider = CaptureGeometry,

            // What gets WRITTEN is the whole family, not this window: a shell
            // on its own still answers with its own single entry, so every
            // view-model test is unchanged and the two paths cannot drift.
            WholeSession = _services.Compose,

            // And the eject veto asks every window, because a per-window answer
            // would let this one "safely remove" a stick another is filling.
            AllRunning = () => _services.Running,

            // The tab this window was opened FROM, so it arrives in the view it
            // was opened from rather than resetting five settings.
            LikeTab = like,
        };

        // After the shell exists, deliberately: Compose() and Running both ask
        // every adopted window for its shell, and a window in the list before
        // it has one would be a null nobody can guard against without lying to
        // the compiler about the field's type.
        _services.Adopt(this);
        _shell.PaneCreated += (_, pane) => WirePane(pane);
        _shell.PropertiesRequested += (_, _) => ShowProperties();

        // A sidebar row names its own path rather than a selection, so it takes
        // the same dialog by a different route.
        _shell.ShowPropertiesRequested += (_, path) => ShowPropertiesFor(path);
        _shell.SettingsRequested += (_, _) => ShowSettings();

        // "Use this view for all folders" has already applied the change and
        // emptied the per-folder store; the file is the half only the window can
        // write. Through the same store the settings dialog saves with, so the
        // two routes cannot disagree about where preferences live.
        _shell.DefaultViewChanged += (_, settings) => _services.SettingsStore.Save(settings);

        // A dragged column width, once the drag has ended: the same route for
        // the same reason.
        _shell.ColumnWidthsChanged += (_, settings) => _services.SettingsStore.Save(settings);
        _shell.EmptyTrashRequested += (_, _) => AskConfirmEmptyTrash();
        _shell.CopyAcrossRequested += (_, plan) => AskConfirmCopyAcross(plan);

        // Exactly what Delete does, so the menu and the key cannot disagree
        // about whether the confirmation setting applies.
        _shell.TrashSelectionRequested += (_, _) =>
        {
            if (_shell.ActiveTab is not { } pane) return;

            if (AppSettings.Current.General.ConfirmMoveToTrash) AskConfirmTrash();
            else pane.TrashSelectedCommand.Execute(null);
        };
        _shell.GrowRequested += (_, by) => GrowToFit(by);
        _shell.ReleaseRequested += (_, _) => ReleaseGrownWidth();
        _shell.BatchRenameRequested += (_, _) => ShowBatchRename();
        _shell.UseRemotes(platform.Remotes);

        _shell.UseDriveLinks(
            _services.DriveLinks, _services.DriveLinkStore.Load(),
            links => _services.DriveLinkStore.Save(links),
            url => _launcher?.Open(url));
        _shell.UseDiscovery(platform.Discovery);
        _shell.UseProperties(platform.Properties);

        _shell.ConnectionInfoRequested += (_, info) =>
            new ConnectionWindow(info).ShowDialog(this);

        _shell.ShortcutsRequested += (_, _) => new ShortcutsWindow().ShowDialog(this);
        _shell.TourRequested += (_, _) => new TourWindow().ShowDialog(this);
        _shell.PaletteRequested += (_, _) => _ = RunFromPaletteAsync();

        _shell.RenamePlaceRequested += OnRenamePlaceRequested;

        // The last row of Copy to and Move to. The shell decides there is a
        // folder to ask for; only the window can ask.
        _shell.TransferBrowseRequested += OnTransferBrowseRequested;

        // Closing the last tab, and Ctrl+Q. The window is the only thing that
        // can close itself, and closing it runs the ordinary shutdown — the
        // session is saved on the way out exactly as it is for the title-bar
        // button.
        _shell.CloseRequested += (_, _) => Close();

        // A PEER, not a child. The shell names the folder; the window builds
        // the thing, because a view model has no business constructing one.
        _shell.NewWindowRequested += (_, folder) => OpenNewWindow(folder);

        // **The question that was never asked.** Copy and move have understood
        // Overwrite, Skip and Cancel since they were written, and every caller
        // passed KeepBoth outright — so a newer file dropped over an older one
        // silently became "name (1)".
        //
        // Marshalled, because the operation runs on a background thread and is
        // awaiting the answer: a dialog opened from there would touch the UI
        // from the wrong thread, and awaiting it from the UI thread is what
        // lets the copy carry on afterwards.
        ViewModels.PaneViewModel.AskConflict = async conflict =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var model = new ViewModels.ConflictViewModel(conflict);

                // **A process-wide static that every window assigns.** It
                // captured `this`, so with two windows the prompt belonged to
                // whichever was constructed LAST — and after that one closed,
                // to a window that is gone. Resolved when the question is
                // asked instead, falling back to the window that assigned it.
                var owner = _services.Active ?? this;

                // ShowDialog returns when the window closes; the window closes
                // when the model answers, and closing it any other way answers
                // Cancel. So this cannot wait forever on a dismissed dialog.
                await new ConflictWindow(model).ShowDialog(owner);

                return await model.Answer;
            });

        _shell.ShareDialogRequested += (_, request) =>
            new ShareWindow(request).ShowDialog(this);

        _shell.ConnectRequested += OnConnectRequested;

        // The clipboard belongs to the view, so the shell asks rather than reaches.
        _shell.CopyTextRequested += async (_, url) =>
        {
            try
            {
                if (Clipboard is { } clipboard) await clipboard.SetTextAsync(url);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vaktari] clipboard: {ex.Message}");
            }
        };

        _shell.ScaleApplier = ApplyScales;
        DataContext = _shell;

        // The keys that answer anywhere in the window, from the keymap in
        // force — and again whenever a save moves one. See MainWindow.Keyboard.cs.
        InstallKeymap();
        WatchKeymap();

        // The rename keys are answered on the window's tunnel now, because the
        // box they belong to is drawn by the listing's item template rather
        // than named here; nothing else the bar still shows has keys of its own
        // to hang on this control.
        PromptConfirm.Click += (_, _) => ConfirmPrompt();
        PromptCancel.Click += (_, _) => ClosePrompt();

        // Handled at the window because the list lives inside a DataTemplate,
        // so there is no named control to attach to.
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);

        // Its single-click twin. Both are always registered and the preference
        // is read at gesture time, because the rows live inside a DataTemplate
        // and there is no list of realized controls to re-attach when the
        // setting changes.
        AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble);

        // Type-ahead. TextInput rather than KeyDown, because it is the event
        // that already accounts for keyboard layout, shift and dead keys — a
        // Key enum says "D", TextInput says "d" or "D" or "é" as typed.
        AddHandler(TextInputEvent, OnWindowTextInput, RoutingStrategies.Bubble);

        // Tab has to be caught on the way DOWN. Keyboard navigation claims it
        // before any bubble handler runs, so by the time the window sees it
        // focus has already left the box. Tunnel reaches the window first.
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);

        // The row's rename box has no field to hang a LostFocus on either.
        AddHandler(LostFocusEvent, OnRenameBoxLostFocus, RoutingStrategies.Bubble);

        // Clicking anywhere in a side makes it the active one. Tunnelling so it
        // runs before the ListBox handles the press for selection — otherwise
        // the first click on an inactive side only moves focus.
        AddHandler(PointerPressedEvent, OnPointerPressedAnywhere, RoutingStrategies.Tunnel);

        // A right-drag ends in a right-button release, and a release is how a
        // context menu opens. Tunnelled so the suppression wins everywhere.
        AddHandler(ContextRequestedEvent, (_, e) =>
        {
            if (!_suppressContextMenu) return;

            _suppressContextMenu = false;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedAnywhere, RoutingStrategies.Tunnel);
        AddHandler(
            PointerReleasedEvent,
            (_, e) =>
            {
                EndBand();
                EndTabDrag();

                // **A place row is a Button, and a Button clicks on release.**
                // Without this the drop navigates: you drag a pin into its new
                // position, let go, and the sidebar takes you to whatever row
                // the pointer ended over. Claimed on the tunnel, which runs
                // before the button sees the release. Only after a reorder that
                // really moved — an ordinary click on a pinned place arms the
                // drag too, and must still open it.
                if (EndPlaceDrag(save: true)) e.Handled = true;
            },
            RoutingStrategies.Tunnel);

        // Tunnel, so the gesture is claimed before the listing's ScrollViewer
        // sees it — otherwise the view zooms and scrolls at the same time.
        AddHandler(PointerWheelChangedEvent, OnWheelAnywhere, RoutingStrategies.Tunnel);

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Dragging the splitter writes straight to the ColumnDefinitions, so
        // the ratio is read back out afterwards — otherwise the persisted
        // SplitRatio would never reflect where the divider actually sits.
        SplitHandle.DragCompleted += (_, _) => CaptureSplitRatio();

        // The sidebar's handle has to move the width itself, unlike the one
        // above: a Thumb reports how far it was dragged and changes nothing,
        // where a GridSplitter edits the definitions it sits between. The
        // sidebar is in a DockPanel and has none, which is why it is a Thumb.
        //
        // Clamped rather than free. Below about 150 the group headings and the
        // drive sizes collide; above 520 it is taking room from the listing,
        // which is what the window is for. The value is an [ObservableProperty]
        // that the session already saves and restores, so a width set here
        // survives a restart with no further work.
        SidebarHandle.DragDelta += (_, e) =>
            _shell.Sidebar.Width = Math.Clamp(_shell.Sidebar.Width + e.Vector.X, 150, 520);

        // **The founder subscribes, and no handler captures it.** These three
        // are the desktop's roles and they belong to the APPLICATION: closing
        // the window that happened to start it must not take "open folder" off
        // the desktop for the rest of the session, so each one resolves the
        // window when the request lands rather than when it is wired.
        if (founder)
        {
            var services = _services;

            // A folder named on the command line, and any handed over by a
            // later launch. Without this the window ignored the path it was
            // asked for, which as a default file manager is the whole job.
            if (Program.Instance is { } instance)
                instance.PathsReceived += (_, paths) =>
                    services.ForDesktopRequest?.OpenPaths(paths, activate: true);

            // The same request as a handed-over launch, arriving by the other
            // route the desktop has for it — and the one a browser's "show in
            // folder" actually uses, because a launch cannot express "and
            // select this file".
            //
            // **Only the instance that owns the single-instance lock answers.**
            // A window opened by an instance that LOST the lock is a temporary
            // second copy, and a second copy claiming a desktop-wide role would
            // take "show in folder" with it and hold it for as long as it
            // lived.
            if (Program.Instance is not null && platform.FileManagerService is { } fileManager)
            {
                services.FileManager = fileManager;

                // Posted, not called: this is raised from the bus's own read
                // loop, which reads no further messages until the handler
                // returns, and everything it leads to opens tabs and touches
                // the window.
                fileManager.Requested += (_, request) =>
                    Dispatcher.UIThread.Post(() => services.ForDesktopRequest?.OnShowRequested(request));

                Dispatcher.UIThread.Post(() => AnnounceFileManagerService(fileManager));
            }

            if (Program.StartupPaths.Length > 0)
                Dispatcher.UIThread.Post(
                    () => services.ForDesktopRequest?.OpenPaths(Program.StartupPaths, activate: false));
        }

        // Held for the settings dialog, which is per window and would otherwise
        // hand a secondary window a null and show something different from the
        // founder's.
        _fileManager = _services.FileManager;

        Closing += OnClosing;

        // Teardown is a real event now that a window is not the process. What
        // OnClosed lets go of is measured in its own comment.
        Closed += OnClosed;

        // Which window a desktop request and a conflict prompt belong to. A
        // focus event, so it may never have fired — every reader falls back.
        Activated += (_, _) => _services.Active = this;
        Resized += (_, _) => _shell.NotifyWindowChanged();
        PositionChanged += (_, _) => _shell.NotifyWindowChanged();

        // Applied before Start so the first paint is already at the right size.
        // A window opened from another reads its OPENER's scales, not the
        // session's — it is not being restored from anything.
        var geometry = seed ?? state?.Windows.ElementAtOrDefault(restoreIndex);
        ApplyScales(
            geometry?.FontScale is > 0 and var f ? f : 1.0,
            geometry?.IconScale is > 0 and var i ? i : 1.0);

        // The startup setting decides whether the session is consulted at all,
        // which is the whole reason settings are loaded before it. Restoring
        // stays the default: forgetting open folders is the complaint this
        // project exists to answer.
        var startup = _settings.Startup;

        var restore = startup.ShowOnStartup == StartupLocation.RestoreSession;

        var openFolder = openAt ?? startup.ShowOnStartup switch
        {
            StartupLocation.SpecificFolder when
                !string.IsNullOrWhiteSpace(startup.StartupFolder)
                && Directory.Exists(startup.StartupFolder) => startup.StartupFolder,

            // **The drive listing had no way in from here.** Every other route
            // to it — the sidebar row, Up from a drive root, its breadcrumb,
            // typing its name in the path bar — has existed since the listing
            // was written, and the one place that decides where a LAUNCH
            // begins could only name a directory. Explorer opens on This PC or
            // on Home, so this is the choice a Windows user arrives expecting.
            //
            // Not routed through the arm above: that one is gated on
            // Directory.Exists, and "vaktari:computer" is not a directory, so
            // it fell to the fallback below and opened home.
            StartupLocation.Computer => VirtualPaths.Computer,

            // A configured folder that no longer exists falls back to home
            // rather than opening nothing — an unremovable empty window would
            // be a worse failure than ignoring a stale path.
            _ => null,
        };

        // **But not QUIETLY, which is what it used to be.** A typo, or a path
        // on a drive that has been repartitioned, opened home and said nothing
        // — indistinguishable from the setting not working at all. The window
        // still opens where it can; it just admits that it is not where it was
        // asked to be.
        var startupFolderGone =
            openAt is null
            && startup.ShowOnStartup == StartupLocation.SpecificFolder
            && !string.IsNullOrWhiteSpace(startup.StartupFolder)
            && !Directory.Exists(startup.StartupFolder);

        // **A window opened from another one is not a launch.** The startup
        // preference answers "where does a LAUNCH begin"; this one was asked
        // for a folder and handed the view to arrive in, so the seed is dressed
        // up as a one-window session and Start's existing restore path applies
        // it — the sidebar width and rail, the folded sections, the split ratio
        // and the two scales. Its Panes list is empty, so the tab comes from
        // openFolder rather than from anything the opener had open.
        var from = seed is not null
            ? new SessionState { Windows = [seed] }
            : restore ? state : null;

        _shell.Start(from, openFolder, seed is not null ? 0 : restoreIndex);

        // After Start, so the line is not written into a shell that is about to
        // build its first pane over it.
        if (startupFolderGone)
        {
            _shell.OperationStatus =
                $"{PathRules.LeafName(startup.StartupFolder!)} is not there — opened your "
                + "home folder instead. Settings still has the folder you chose.";
        }

        ApplyStartupPreferences(startup);

        _services.StartTrashMaintenance(platform.TrashMaintenance);

        // The sidebar was built before the bin was installed and asked an
        // absent one, so the row starts on the empty glyph however full the bin
        // is. Per window, because a sidebar is.
        _shell.Sidebar.RefreshBinState();

        // **Gated on `restore`, not only on `founder`.** Without that gate,
        // Home and SpecificFolder opened window 0 correctly — ignoring the
        // session, as the preference asks — and then opened N-1 more windows
        // restored out of the very session the preference had just said not to
        // consult.
        //
        // Posted rather than called: constructing a window inside a window's
        // constructor is a re-entrancy nobody wants to reason about.
        if (founder && restore && state is { } saved)
        {
            for (var next = 1; next < saved.Windows.Count; next++)
            {
                var index = next;
                Dispatcher.UIThread.Post(() => RestoreWindow(index));
            }
        }

        // Build stamp AND the binary it came from. When a symptom and the code
        // disagree, these two lines say whether the running program contains the
        // fix — and, just as often, whether it is the program you think it is.
        //
        // **The path earns its place.** A `~/.local` install from `install.sh`
        // and an RPM coexist happily: nothing is shared, and `~/.local/bin`
        // precedes `/usr/bin`, so a stale user install silently wins over every
        // package upgrade. That cost a round of "the new feature is missing from
        // the RPM" when the RPM was never the thing running.
        Console.Error.WriteLine(
            $"[vaktari] build {BuildStamp()}  clipboard=yes  split={_shell.IsSplit}");

        Console.Error.WriteLine($"[vaktari] running {Environment.ProcessPath ?? "(unknown)"}");
    }

    private static string BuildStamp()
    {
        try
        {
            // Two candidates, in order, because the managed assembly does not
            // exist as a file in a NativeAOT publish — it is compiled into the
            // executable. Stamping only the dll meant this read "unknown" in
            // precisely the build where there is no other way to tell which
            // binary is running.
            string?[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "Vaktari.Ui.dll"),
                Environment.ProcessPath,
            ];

            foreach (var candidate in candidates)
                if (candidate is { Length: > 0 } && File.Exists(candidate))
                    return File.GetLastWriteTime(candidate).ToString("HH:mm:ss");

            return "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Adds one row to the selection, or takes it out, leaving every other row
    /// exactly as it was.
    ///
    /// Through the bound collection rather than through <c>SelectedItem</c>:
    /// assigning that REPLACES the selection, which is precisely what a tick
    /// box must never do. One row at a time, so unlike SelectAll there is no
    /// bulk path worth reaching for — a single change fires a single
    /// notification.
    /// </summary>
    internal static void ToggleInSelection(ListBox list, object row)
    {
        if (list.SelectedItems is not { } chosen) return;

        if (chosen.Contains(row)) chosen.Remove(row);
        else chosen.Add(row);
    }

    /// <summary>
    /// Focuses the enclosing ListBox when a press lands inside it but not on an
    /// item, so the keyboard has somewhere to start.
    ///
    /// This was a NO-OP for as long as it existed, because a ListBox is not
    /// focusable by default and `Focus()` simply returned false — a silent
    /// refusal that made the fix look shipped when it had never run. Focus
    /// stayed wherever it was, typically a toolbar button, and Home and End
    /// never reached the panel at all. The three listing ListBoxes now carry
    /// `Focusable="True"` explicitly, which is what makes this work.
    /// </summary>
    private static void FocusListIfEmptySpace(object? source, KeyModifiers modifiers)
    {
        if (ListForEmptySpace(source) is not { } list) return;

        // Without this, pressing Home or End after clicking below the tiles did
        // nothing: keyboard navigation begins at the focused element.
        if (!list.IsFocused) list.Focus();

        // **And it clears the selection, which nothing did.** Clicking empty
        // space is how every file manager says "never mind" — Avalonia's ListBox
        // does not do it for you, and the band only reaches ApplyBand once the
        // rectangle passes six pixels, so a click that never becomes a drag
        // touched the selection not at all. Ctrl and Shift are excluded: those
        // mean "keep what I have and add to it".
        if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift))
            return;

        list.SelectedItems?.Clear();
    }

    private void OnPointerPressedAnywhere(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        // Ctrl + wheel click resets the pane under the pointer, completing the
        // gesture: wheel to scale, click to undo. Claimed before anything else
        // sees the press, or the listing treats it as a selection.
        //
        // PointerUpdateKind, not IsMiddleButtonPressed: the latter reports the
        // *current state* of that button, which is not the same question as
        // "which button raised this press".
        var properties = e.GetCurrentPoint(this).Properties;

        // **Recorded here because nothing later can see it.** A ContextMenu
        // opening carries no record of which keys were down, and by the time
        // the menu is building, Shift has long been released. Explorer puts its
        // administrator entries behind the same gesture.
        //
        // On the press of the RIGHT button specifically: Shift+left-click is
        // range selection, and letting that arm the section would put elevation
        // one stray right-click away from a perfectly ordinary selection.
        if (properties.PointerUpdateKind is PointerUpdateKind.RightButtonPressed
            && PaneAt(e.Source) is { } target)
        {
            target.AdminRequested = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        }

        // **The mouse's own back and forward buttons.** Explorer navigates on
        // these, every browser navigates on these, and the convention is old
        // enough that the buttons are usually unlabelled — so an application
        // that ignores them reads as broken rather than as opinionated.
        //
        // The pane UNDER THE POINTER, which is the rule Ctrl+wheel already
        // follows: in a split, pointing at a half and pressing back should move
        // that half. Activation is deliberately left alone — a navigation
        // button is not a click, and stealing the active pane would change what
        // the next keystroke does.
        //
        // The strip is chrome rather than a pane, though, and a tab header
        // carries a pane of its own — see NavigationTargetAt.
        //
        // Claimed on the tunnel, before the listing sees the press. Some
        // controls treat any pointer press as a selection gesture, and a
        // side button would then move the selection as well as the folder.
        if (Input.SideButtons.For(properties.PointerUpdateKind) is var side
            && side is not Input.SideButtonAction.None)
        {
            var pane = NavigationTargetAt(e.Source) ?? _shell.ActiveTab;

            if (pane is not null)
            {
                // **Back, not up.** This read GoUpAsync between fee6393 and
                // now, collateral from the commit that made BACKSPACE a
                // choice between the two: the mouse button was swept up in
                // the same edit and nothing said so, because the test beside
                // this one only checked which button maps to which action.
                // The two coincide in most trees, which is why it survived —
                // walk sideways rather than down and they part company.
                _ = side is Input.SideButtonAction.Back
                    ? pane.GoBackAsync()
                    : pane.GoForwardAsync();
            }

            // Handled whether or not there was anywhere to go: at the end of the
            // history the button does nothing, and letting the press fall
            // through to the listing would turn "nothing to go back to" into an
            // accidental change of selection.
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && properties.PointerUpdateKind
               is Avalonia.Input.PointerUpdateKind.MiddleButtonPressed)
        {
            _shell.ResetPaneScale(PaneAt(e.Source) ?? _shell.ActiveTab);

            // The accumulator would otherwise carry leftover travel from before
            // the reset into the next scroll.
            _zoomTravel = 0;

            e.Handled = true;
            return;
        }

        // **Middle click, which everything else on this desktop uses for
        // "open in a new tab" and "close this one".** A browser does it, and so
        // does Explorer now that it has tabs.
        //
        // After the Ctrl+middle reset above, which keeps its own meaning.
        if (properties.PointerUpdateKind is PointerUpdateKind.MiddleButtonPressed)
        {
            if (TabAt(e.Source) is { } tab)
            {
                _shell.CloseTabCommand.Execute(tab);
                e.Handled = true;
                return;
            }

            // Anywhere else it means nothing, and is left alone rather than
            // swallowed — a middle click on the listing background is how some
            // desktops paste.
        }

        // The sidebar, the crumbs and the folder rows, by middle click and — on
        // the two that are not selectable — by Ctrl+click. Behind the current
        // tab, because that is what the gesture means.
        if (NewTabTarget(e.Source, properties.PointerUpdateKind, e.KeyModifiers) is { } opening)
        {
            _shell.OpenBehind(opening);
            e.Handled = true;
            return;
        }

        // **A press on a row's selection box is a tick, not a click on the
        // row**, and the difference is everything the feature is for: the
        // ListBox reads an unmodified press as "just this one" and collapses
        // whatever was chosen down to the row under the pointer. Measured, in
        // the headless harness: with two rows selected, a plain press on a
        // third left one row selected. Taken here, on the tunnel, all three
        // survive.
        //
        // Handled either way, so the press never reaches the ListBox, the
        // rubber band or the drag arm below. That also means a double-click on
        // a box never forms a gesture — two presses, two ticks, back where you
        // started and the file does not open, which is the right answer for a
        // control whose whole job is to not be an activation.
        if (properties.IsLeftButtonPressed && SelectionBoxAt(e.Source) is not null)
        {
            ActivateGroupAt(e.Source);

            if (ListingAt(e.Source) is { } picked && EntryAt(e.Source) is { } row)
                ToggleInSelection(picked, row);

            // **Returning early is not the same as arming nothing.** The fields
            // below survive their press — the release handler clears the band
            // and the tab drag but never _dragSource, and the move handler
            // clears it only on a move with the button up — so a press that
            // returns before the arming block leaves the PREVIOUS press's row
            // and the PREVIOUS press's origin in place. A second press arriving
            // without an intervening move therefore starts from an origin that
            // belonged to a click somewhere else, and a drag from the box would
            // carry the old row.
            ArmNothing();

            e.Handled = true;
            return;
        }

        // **And a press on the expand triangle opens the folder in place**,
        // which is not a click on the row either — for the same reason and with
        // the same consequences as the box above: taken on the tunnel, a
        // multiple selection survives it, and two quick presses are an open and
        // a close rather than a double-click that opens the folder for real.
        //
        // Only on a folder. The slot is the same width on every row so that
        // files and folders keep their icons in one column, and claiming a
        // press over a file's empty slot would put a 16px dead strip down the
        // left of every file in the listing. Unhandled, it falls through to the
        // row underneath it, which is what the rest of the row background does.
        if (properties.IsLeftButtonPressed
            && ExpanderAt(e.Source) is not null
            && PaneAt(e.Source) is { } expanding
            && EntryAt(e.Source) is { IsDirectory: true } folder)
        {
            ActivateGroupAt(e.Source);

            _ = expanding.ToggleExpandAsync(folder);

            ArmNothing();

            e.Handled = true;
            return;
        }

        // A click on empty listing space gives the LIST keyboard focus.
        //
        // Without this, pressing Home or End after clicking below the tiles did
        // nothing: keyboard navigation begins at the focused element, and the
        // press had focused the scroll area rather than the list, so the key
        // never reached the panel at all. Confirmed with a diagnostic — the
        // panel's navigation was called only when a ListBoxItem already had
        // focus, and worked correctly every time it was.
        //
        // Only when the press did NOT land on an item: an item click focuses
        // itself, and stealing that would break selection.
        // **Left button only.** This clears the selection, and it ran for every
        // button — so right-clicking the blank half of a full-width row, which
        // is where people aim for the context menu, collapsed a five-file
        // selection to nothing before the menu opened. The next Delete took one
        // file, or none. Explorer and Dolphin both keep the selection when you
        // right-click inside it.
        if (properties.IsLeftButtonPressed)
            FocusListIfEmptySpace(e.Source, e.KeyModifiers);

        // Recorded here so a drag can start on the first move past the
        // threshold rather than on the press itself.
        _dragOrigin = e.GetPosition(this);

        ArmTabDrag(e, properties);
        ArmPlaceDrag(e, properties);

        // **A drag from empty space is a SELECTION, not a file drag.** Both
        // begin with a left press inside a pane, so the only thing separating
        // them is what sat under the pointer — and arming both would race.
        _bandList = properties.IsLeftButtonPressed ? ListForEmptySpace(e.Source) : null;
        _bandOrigin = e.GetPosition(BandLayer);
        _bandScrollAt = _bandList is { } armed && Scroller(armed) is { } view
            ? view.Offset
            : default;
        _bandTaken.Clear();
        _bandAdditive = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                        || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _bandKept = null;

        // **The right button drags too**, which is Explorer's oldest answer to
        // "did I just move that or copy it": drag with the right button and a
        // menu asks at the drop. Only from a row — a right press on empty
        // space keeps meaning the background menu.
        _dragRight = properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed
                     && EntryAt(e.Source) is not null;

        // **From a ROW, the way the right button already required.** The left
        // arm asked only "is this a pane, and not a band", which is true of the
        // column headings, the tab strip, the transfer bar and the preview
        // overlay — so a six-pixel twitch while pressing any of them began
        // dragging the current selection, and a drop moved real files. The
        // right arm had had EntryAt all along.
        //
        // **And the box a name is typed in is not the row it sits on.** The
        // editor is drawn inside the item template and carries the row's own
        // FileEntry, which is exactly what EntryAt walks for — so dragging
        // across it to select the text armed a real file drag. Measured here:
        // a left press at one end of the box and a move to the other left the
        // window with `_dragging` true and the file on the pointer, and the
        // right button did the same.
        _dragSource =
            !InRenameBox(e.Source)
            && ((properties.IsLeftButtonPressed && _bandList is null && EntryAt(e.Source) is not null)
                || _dragRight)
                ? PaneAt(e.Source)
                : null;
        _dragTrigger = _dragSource is null ? null : e;

        // **Snapshotted before the listing sees the press.** This handler is
        // registered on the tunnel, so the selection here is still what the
        // user had; a moment later the press collapses it to the single row
        // under the pointer and a drag of five files would carry one.
        //
        // Only when the press landed ON something already selected — pressing
        // an unselected row genuinely does mean "just this one", and carrying
        // the old selection there would drag files the user had just moved
        // away from.
        _dragSelection = null;

        if (_dragSource is { } source && EntryAt(e.Source) is { } pressed
            && source.Selection.Count > 1
            && source.Selection.Any(x => PathRules.Same(x.FullPath, pressed.FullPath)))
            _dragSelection = source.Selection.Select(x => x.FullPath).ToList();

        ActivateGroupAt(e.Source);
    }

    /// <summary>
    /// Makes the half of a split that was pressed the active one.
    ///
    /// Visual tree — same reason as EntryAt. A press that lands on templated
    /// content has no logical path back to the group, so clicking a filename
    /// would not activate its side.
    ///
    /// Its own method because the selection-box guard returns before the end of
    /// the press handler and still has to do this: ticking a box in the
    /// inactive half must make that half active, or the next Delete acts on the
    /// other one.
    /// </summary>
    private void ActivateGroupAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PaneGroupViewModel group })
            {
                _shell.ActivateGroup(group);
                return;
            }
        }
    }

    /// <summary>
    /// Forgets everything a press would otherwise have armed.
    ///
    /// Only the selection-box guard needs it, and it needs it because the
    /// arming block sits at the END of the press handler: anything that returns
    /// before reaching it inherits the last press's state rather than a blank
    /// one.
    /// </summary>
    private void ArmNothing()
    {
        _bandList = null;
        _bandKept = null;
        _dragSource = null;
        _dragTrigger = null;
        _dragSelection = null;
        _dragRight = false;
    }

    private void CaptureSplitRatio()
    {
        if (!_shell.IsSplit) return;

        var left = SplitGrid.ColumnDefinitions[0].ActualWidth;
        var right = SplitGrid.ColumnDefinitions[2].ActualWidth;
        var total = left + right;

        if (total > 1) _shell.SplitRatio = Math.Clamp(left / total, 0.1, 0.9);
    }

    // ---- ui scale ------------------------------------------------------

    /// <summary>
    /// Application-level defaults, used by everything outside a pane — the
    /// sidebar, the status bar, the properties window. Each pane overrides
    /// these with its own dictionary via PaneScale.
    /// </summary>
    private void ApplyScales(double fontScale, double iconScale)
    {
        var target = Application.Current?.Resources ?? Resources;

        foreach (var (key, value) in PaneScale.Compute(fontScale, iconScale))
            target[key] = value;
    }

    /// <summary>
    /// Everything a desktop scheme change moves that is not a colour: the
    /// application-level metrics and each pane's own.
    ///
    /// The desktop's text size arrives on the palette, and every size in the
    /// window multiplies it, so the read that repaints has to be followed by
    /// the arithmetic that resizes.
    ///
    /// **A method rather than two lines inside the handler**, because the
    /// handler is built in the constructor — where <c>_shell</c> has not been
    /// assigned yet, so the compiler rightly refuses to let a lambda defined
    /// there dereference it.
    /// </summary>
    private void RescaleForDesktop()
    {
        ApplyScales(_shell.FontScale, _shell.IconScale);
        _shell.RefreshPaneScales();
    }

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
    /// </summary>
    internal void SettingsChangedEverywhere()
    {
        // Over a copy: OnSettingsChanged reaches most of a window, and the
        // family list is the one every window adds itself to and removes itself
        // from — iterating it live would be trusting that none of that runs.
        foreach (var window in _services.Windows.ToList())
            window.Shell.OnSettingsChanged();
    }

    /// <summary>
    /// Modal, unlike properties: a rename changes the very listing behind it,
    /// so letting the window sit open over a view that is mutating underneath
    /// would show a plan built from names that no longer exist.
    /// </summary>
    private void ShowBatchRename()
    {
        if (_shell.ActiveTab is not { } pane) return;

        var entries = pane.Selection.Count > 0
            ? pane.Selection.ToList()
            : pane.SelectedEntry is { } one ? [one]
            : new List<FileEntry>();

        if (entries.Count == 0)
        {
            pane.Status = "select something to rename first";
            return;
        }

        // The throwing one: the dialog counts what it renamed and stops at the
        // first refusal, which it can only do if a refusal reaches it.
        //
        // The group is what makes the whole dialog one press of Ctrl+Z: without
        // it the engine recorded a step per file, and a swap recorded more
        // steps than there were files.
        var model = new BatchRenameViewModel(entries,
            (entry, name) => pane.RenameOrThrowAsync(entry, name),
            pane.Entries,
            pane.BeginRenameGroup);

        new BatchRenameWindow(model).ShowDialog(this);
    }

    /// <summary>
    /// A folder for a picker to open at, or null where there is nothing there
    /// yet and the picker should choose for itself.
    ///
    /// A member rather than the local function it began as, because the
    /// transfer submenus' "Choose a folder…" opens a picker from outside the
    /// settings dialog and wants the same answer to the same question.
    /// </summary>
    private static async Task<Avalonia.Platform.Storage.IStorageFolder?> Suggested(
        Window window, string path)
    {
        try
        {
            return Directory.Exists(path)
                ? await window.StorageProvider.TryGetFolderFromPathAsync(path)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// "Choose a folder…" at the foot of Copy to and Move to.
    ///
    /// The picker belongs to the window for the reason the settings dialog's
    /// does: a view model that opened one could not be constructed in a test.
    /// The shell is already holding the files and the direction, so the only
    /// thing that travels back is the folder — and a dismissed picker returns
    /// nothing and does nothing.
    /// </summary>
    private async void OnTransferBrowseRequested(
        object? sender, ViewModels.TransferBrowseRequest request)
    {
        // Starting where the files are: a destination is more often beside the
        // source than at home, and it is the folder the user can see.
        var start = await Suggested(this, request.StartAt);

        var picked = await StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = request.Move ? "Choose a folder to move to" : "Choose a folder to copy to",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });

        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;

        request.Chose(folder);
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
            // the old rule, and a visibility binding needs telling.
            SettingsChangedEverywhere();

            _fullPathInTitle = model.Result.Startup.ShowFullPathInTitleBar;
            RefreshTitle();
        };

        window.ShowDialog(this);
    }

    /// <summary>
    /// Non-modal on purpose: you frequently want to compare two files, and a
    /// modal dialog makes that impossible without closing it first.
    /// </summary>
    private void ShowProperties()
    {
        if (_shell.ActiveTab is not { } pane) return;

        var paths = pane.Selection.Count > 0
            ? pane.Selection.Select(x => x.FullPath).ToList()
            : pane.SelectedEntry is { } one ? [one.FullPath]
            : new List<string> { pane.CurrentPath };

        if (paths.Count == 0) return;

        ShowPropertiesFor(paths);
    }

    private void ShowPropertiesFor(string path) => ShowPropertiesFor([path]);

    private void ShowPropertiesFor(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        // **A sheet for a path that has gone was confidently wrong rather than
        // empty.** Windows answers a query about a file that is not there with
        // a size of zero, 1601-01-01 for every date, and every attribute set —
        // so the window filled itself in and looked authoritative. A row can go
        // between being listed and being asked about, so refusing the bin and
        // Recent is not enough on its own; this is a race as well as a gate.
        var live = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();

        if (live.Count == 0)
        {
            if (_shell.ActiveTab is { } gone)
                gone.Status = paths.Count == 1
                    ? $"{PathRules.LeafName(paths[0])} is no longer there"
                    : "those items are no longer there";

            return;
        }

        paths = live;

        // **The desktop's own dialog wins where it has one.** On Windows that
        // sheet carries Security, Details and the Unblock checkbox, and hosts
        // the pages other applications add to the shell — none of which this
        // application can reproduce, and all of which are why somebody opens
        // properties there.
        //
        // One path only. The shell has SHMultiFileProperties for a selection,
        // but it wants an ITEMIDLIST array rather than paths and shows a
        // reduced sheet; a multi-select falls through to Vaktari's window,
        // which handles several items properly already.
        if (paths.Count == 1 && _properties.ShowSystemDialog(paths[0])) return;

        // Theme and metrics are application-scoped, so this inherits them.
        new PropertiesWindow(new PropertiesViewModel(_properties, paths, _accessEditor)).Show(this);
    }

    /// <summary>
    /// Feeds the group its own width, which is what decides whether the details
    /// panel fits.
    ///
    /// Measured rather than derived from the listing: the listing's width already
    /// excludes the panel when the panel is shown, so testing it would have been
    /// circular — and adding the panel's width back depended on knowing whether
    /// it was shown, which is the question.
    /// </summary>
    private void OnGroupSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control { DataContext: PaneGroupViewModel group })
            group.GroupWidth = e.NewSize.Width;
    }

    /// <summary>
    /// Widens or narrows the details panel as its handle is dragged.
    ///
    /// **The panel had a resize handle that could not resize anything.** It was
    /// a GridSplitter, and a splitter works by editing the Row or
    /// ColumnDefinitions it sits between — the group is a DockPanel, which has
    /// neither, so every drag was inert while the bar still painted itself and
    /// still showed a west-east cursor. The sidebar hit the same trap and was
    /// fixed the same way: a Thumb, and the width moved from here.
    ///
    /// The clamping lives in the group, which owns the width and the rule that
    /// bounds it. This is the plumbing: which side was dragged, and how far.
    /// From the DataContext rather than a name because in split view there are
    /// two of these, and a name would find one.
    /// </summary>
    private void OnInfoHandleDrag(object? sender, VectorEventArgs e)
    {
        if (sender is Control { DataContext: PaneGroupViewModel group })
            group.ResizeInfoBy(e.Vector.X);
    }

    /// <summary>
    /// Widens or narrows a details column as the grip on its heading is
    /// dragged. The same plumbing as the two handles above — which column,
    /// from the Thumb's Tag, and how far — with the arithmetic in the shell.
    ///
    /// **The pane's own zoom goes with it.** The grip reports pixels on
    /// screen and the width is kept at 100%, and it is THIS pane's scale the
    /// column under the pointer was drawn at: in a split at two zooms, the
    /// other side's would move the column a different distance from the
    /// pointer. From the DataContext rather than a name for the reason the
    /// info handle gives: in split view there are two of these, and a name
    /// would find one.
    /// </summary>
    private void OnColumnGripDragDelta(object? sender, VectorEventArgs e)
    {
        if (sender is Control { DataContext: PaneViewModel pane, Tag: string column })
            _shell.ResizeColumn(Enum.Parse<DetailsColumn>(column), e.Vector.X, pane.FontScale);
    }

    private void OnColumnGripDragCompleted(object? sender, VectorEventArgs e)
        => _shell.CommitColumnWidths();

    /// <summary>
    /// Opens the command palette and runs what it picked.
    ///
    /// **Run AFTER the palette has closed, not from inside it.** Half the
    /// commands move the keyboard — into the address bar, the filter, a
    /// rename — and a command run while the palette is still the active
    /// window puts the caret in a box the palette is about to take the
    /// focus back from on its way out. The pick is read once the dialog has
    /// returned, when this window is in front again.
    /// </summary>
    private async Task RunFromPaletteAsync()
    {
        var palette = new PaletteWindow();

        await palette.ShowDialog(this);

        palette.Chosen?.Run(this);
    }

    /// <summary>
    /// Keeps a sidebar place from opening a menu with nothing in it.
    ///
    /// **Avalonia opens a ContextMenu whether or not any child is visible.**
    /// The only entry is "Remove from places", which means nothing on the rows
    /// the user did not put there — Home, Documents, the drives, the shares —
    /// so gating it left every one of those rows popping a 2px sliver of menu
    /// background at the cursor. On a fresh install with no pins, that is every
    /// row in the sidebar.
    ///
    /// Cancelling here is the only hook that stops the popup rather than its
    /// contents — so every entry the menu gains needs an arm here too.
    ///
    /// **Eject is the second such entry, and it is why this now asks two
    /// questions.** The old rule cancelled on every row that was not user
    /// pinned, which is every DRIVE row — precisely the rows eject exists for.
    /// Adding the entry without touching this would have made the whole
    /// context-menu route inert, and silently: a cancelled ContextMenu is not
    /// an error, it is simply a menu that never appears.
    /// </summary>
    private void OnPlaceMenuOpening(object? sender, CancelEventArgs e)
    {
        // **A ContextMenu is its own popup root and inherits no DataContext.**
        // This asked `sender` for one, got null every single time, and so
        // cancelled the menu for EVERY row — the pinned ones and the ejectable
        // drives it was written to allow included. The sidebar menu had never
        // opened for anything, and had it opened, CanEject, IsUserPinned and
        // every CommandParameter inside it would have bound against nothing.
        //
        // PlacementTarget is no help either: it is still null when Opening
        // fires. OnPlaceContextRequested below hands the row over first, from
        // the button, where the DataContext is real.
        if (sender is ContextMenu { DataContext: PlaceItemViewModel row }
            && row.Path.Length > 0) return;

        e.Cancel = true;
    }

    /// <summary>
    /// Gives a sidebar row's menu the row, before it opens.
    ///
    /// The button is the only place the DataContext is real — the menu is a
    /// separate popup root and inherits nothing — and ContextRequested is the
    /// one event raised on the button while there is still time to act.
    /// </summary>
    private void OnPlaceContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: PlaceItemViewModel row, ContextMenu: { } menu })
            menu.DataContext = row;
    }

    /// <summary>Feeds the pane its own width so columns can drop out in
    /// priority order rather than being squeezed.</summary>
    private void OnListSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control { DataContext: PaneViewModel pane })
            pane.ViewportWidth = e.NewSize.Width;
    }

    // ---- what the gestures share ---------------------------------------

    /// <summary>The list a band is being drawn in, or null when none is.</summary>
    private ListBox? _bandList;

    private Point _dragOrigin;

    private void OnPointerMovedAnywhere(object? sender, PointerEventArgs e)
    {
        if (_bandList is not null)
        {
            UpdateBand(e);
            return;
        }

        // **Before the file drag gives up.** A tab arms no _dragSource -- it is
        // not a row -- so a block placed after the line below would never run
        // at all, which is why a tab drag did nothing. The order here is
        // load-bearing.
        if (_tabDrag is not null)
        {
            DragTab(e);
            return;
        }

        if (_placeDrag is not null)
        {
            DragPlace(e);
            return;
        }

        if (_dragging || _dragSource is null) return;

        var held = e.GetCurrentPoint(this).Properties;

        if (!(_dragRight ? held.IsRightButtonPressed : held.IsLeftButtonPressed))
        {
            _dragSource = null;
            _dragTrigger = null;
            _dragSelection = null;
            _dragRight = false;
            return;
        }

        // A threshold, or every click on a row would begin a drag and the list
        // would become impossible to select in.
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragOrigin.X) < 6 &&
            Math.Abs(position.Y - _dragOrigin.Y) < 6) return;

        if (_dragTrigger is not null) _ = BeginDragAsync(_dragSource, _dragTrigger);
    }

    // ---- per-pane wiring -----------------------------------------------

    /// <summary>
    /// Startup preferences that act on the window once it exists. Separate from
    /// the restore decision above because these apply whether or not a session
    /// was restored.
    /// </summary>
    private void ApplyStartupPreferences(StartupSettings startup)
    {
        _fullPathInTitle = startup.ShowFullPathInTitleBar;
        RefreshTitle();

        if (startup.BeginInSplitView && !_shell.IsSplit)
            _shell.ToggleSplitCommand.Execute(null);

        if (_shell.ActiveTab is not { } pane) return;

        if (startup.ShowFilterBar) pane.IsFilterVisible = true;

        // Last, because BeginEditPath takes focus and anything set afterwards
        // would be fighting it for the caret.
        if (startup.LocationBarEditable) pane.BeginEditPath();
    }

    private void WirePane(PaneViewModel pane)
    {
        pane.RenameRequested -= OnRenameRequested;
        pane.RenameRequested += OnRenameRequested;

        pane.ChooseApplicationRequested -= OnChooseApplicationRequested;
        pane.ChooseApplicationRequested += OnChooseApplicationRequested;

        pane.RunFileRequested -= OnRunFileRequested;
        pane.RunFileRequested += OnRunFileRequested;

        pane.PropertyChanged -= OnPaneEditorClosed;
        pane.PropertyChanged += OnPaneEditorClosed;

        pane.PropertyChanged -= OnRenameTyped;
        pane.PropertyChanged += OnRenameTyped;
    }

    /// <summary>
    /// Draws the chooser the desktop does not have one of.
    ///
    /// Modal, because picking an application is the only thing to do while it
    /// is up and the file opens the moment one is picked. Windows never reaches
    /// here: its launcher shows the shell's own dialog and the pane stops
    /// there.
    /// </summary>
    private void OnChooseApplicationRequested(object? sender, ChooseApplicationViewModel model)
        => new ChooseApplicationWindow(model).ShowDialog(this);

    /// <summary>
    /// Asks before a double-click starts a program.
    ///
    /// Modal, like the chooser: there is exactly one thing to decide and the
    /// file goes nowhere until it is decided. Windows never reaches here — its
    /// launcher answers no to CanRunFile, because the shell already runs an
    /// executable that is double-clicked and puts its own warning up first.
    /// </summary>
    private void OnRunFileRequested(object? sender, RunFileViewModel model)
        => new RunFileWindow(model).ShowDialog(this);

    /// <summary>
    /// Hands the keyboard back to the listing when an inline editor closes.
    ///
    /// **Every one of them used to drop focus on the floor.** Press Escape in
    /// the filter, or Enter in the path box, and focus was left on a control
    /// that had just been collapsed to nothing — so the arrow keys, Enter,
    /// Delete, Home, End and type-ahead were all dead until F6 or a click.
    /// Explorer and Dolphin both put the keyboard back in the view.
    /// </summary>
    private void OnPaneEditorClosed(object? sender, PropertyChangedEventArgs e)
    {
        // **Navigating left the keyboard on whatever was clicked.** A
        // breadcrumb, a sidebar place, Back, Up — each is a Button, and it kept
        // focus afterwards, so the arrow keys did nothing and Enter re-clicked
        // the button that had just taken you somewhere. Both references put the
        // keyboard in the listing once you arrive.
        if (e.PropertyName == nameof(PaneViewModel.CurrentPath))
        {
            RefreshTitle();
            FocusListingSoon();
            return;
        }

        if (e.PropertyName is not (nameof(PaneViewModel.IsFilterVisible)
                                   or nameof(PaneViewModel.IsPathEditing)))
            return;

        if (sender is not PaneViewModel pane) return;

        // Only on the way out. Both boxes focus themselves on the way in.
        if (pane.IsFilterVisible || pane.IsPathEditing) return;

        FocusListingSoon();
    }

    /// <summary>
    /// Puts the keyboard back in the listing on the next pass, and only if
    /// nothing else has claimed it.
    ///
    /// Posted because the control being left is still collapsing: focusing now
    /// measures against a tree that is about to change. Guarded on the focused
    /// element because closing one editor is sometimes how another one opens —
    /// Ctrl+F from the path box moves the keyboard to the search box ON
    /// PURPOSE, and snatching it back would be worse than leaving it nowhere.
    /// </summary>
    private bool _fullPathInTitle;

    /// <summary>
    /// The window's title, from the folder on screen.
    ///
    /// **It never followed navigation.** The title was worked out twice — once
    /// at startup and once after the settings dialog closed — so with the
    /// full-path option on it named the startup folder for the whole session,
    /// and with it off the title bar read "Vaktari" and nothing else, ever.
    ///
    /// That is not really about the title bar. The taskbar button and the
    /// alt-tab list show the same string, and that is where a window's title
    /// earns its keep: with four of these open there were four identical
    /// entries and no way to tell which was which without looking inside.
    ///
    /// The folder's name always; the whole path only when it was asked for.
    /// </summary>
    private void RefreshTitle() => Title = TitleFor(_shell.ActiveTab, _fullPathInTitle);

    /// <summary>The string itself, apart from the window, so it can be read
    /// without building one.</summary>
    private static string TitleFor(PaneViewModel? pane, bool fullPath)
    {
        if (pane is null || pane.CurrentPath.Length == 0) return "Vaktari";

        var shown = fullPath ? pane.DisplayPath : pane.Title;

        return shown.Length > 0 ? $"{shown} — Vaktari" : "Vaktari";
    }

    private void FocusListingSoon()
        => Dispatcher.UIThread.Post(
            () =>
            {
                if (FocusManager?.GetFocusedElement() is TextBox) return;

                ActiveListing()?.Focus();
            },
            DispatcherPriority.Background);
}
