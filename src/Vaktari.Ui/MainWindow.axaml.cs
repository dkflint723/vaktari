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

    private bool _closeApproved;

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

                    // Icons follow the desktop too: the resolved paths belong to
                    // the old icon theme and every cached drawable has the old
                    // text colour baked into its currentColor.
                    platformIcons?.Reload(palette?.IconTheme);
                    Thumbnails.IconLoader.Invalidate();
                }));
            });

            _theme.Changed += _onThemeChanged;
        }

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
        _shell.EmptyTrashRequested += (_, _) => AskConfirmEmptyTrash();

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

        _shell.RenamePlaceRequested += OnRenamePlaceRequested;

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
    /// <summary>
    /// The list a press landed in, but ONLY if it landed on empty space rather
    /// than on a row. Null for a press on a row, or outside any list.
    ///
    /// The upward walk is the same one `FocusListIfEmptySpace` needed, and for
    /// the same reason: a press on templated content has no logical path back to
    /// the list, so the visual tree is the only route.
    /// </summary>
    internal static ListBox? ListForEmptySpace(object? source)
    {
        ListBoxItem? row = null;

        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            // Content, not background: the name and the icon.
            // Grabbing one of these means "take this file", so a band must not
            // start here or dragging a file out would become impossible.
            if (row is null && visual is TextBlock or Image or Avalonia.Controls.Shapes.Path)
                return null;

            // **The scrollbar lives INSIDE the list**, so the walk used to
            // reach the ListBox from it and call it empty space: pressing the
            // scrollbar cleared the selection, and dragging the thumb drew a
            // rubber band down the side of the listing while the view scrolled
            // under it. Scrolling is not a selection gesture in any file
            // manager.
            if (visual is Avalonia.Controls.Primitives.ScrollBar
                       or Avalonia.Controls.Primitives.Thumb
                       or RepeatButton)
                return null;

            // **And the group heading lives inside a ROW**, so the walk reached
            // the list from it and called it empty space. Measured on a real
            // window with two files already picked out: a press in the middle
            // of the "MD (1)" heading arrived with e.Source =
            // Avalonia.Controls.Presenters.ContentPresenter — so the content
            // refusal above never saw it — this walk answered with the ListBox,
            // the selection went from "a.txt,b.txt" to empty ON THE PRESS, and
            // _bandList came back armed.
            //
            // Which is worse than losing the selection for an instant, and that
            // was measured too: press, move 40px, release, and the selection
            // ended EMPTY — the pointer had left the button, so the click never
            // completed and the band the heading names was never taken. The
            // same three steps with this refusal in place leave "a.txt,b.txt"
            // untouched throughout.
            //
            // Refused here rather than in the two handlers, because one refusal
            // covers both the clearing and the arming: they read the same
            // answer. This is the rule the box and the triangle already keep —
            // a widget drawn inside a row is not the row's background.
            if (visual is Control head && head.Classes.Contains(GroupHeadingClass))
                return null;

            if (visual is ListBoxItem hit) { row = hit; continue; }

            if (visual is not ListBox found) continue;

            // Not every list can hold a multiple selection — the column strip is
            // SelectionMode="None", and a band there would draw a rectangle that
            // selects nothing.
            if (!found.SelectionMode.HasFlag(SelectionMode.Multiple)) return null;

            // Nothing under the pointer but the list itself: always a band.
            if (row is null) return found;

            // **A row that is already selected drags from anywhere on it.**
            // Building a selection and then reaching for it destroyed the
            // selection instead: the gaps around the Size and Date text are
            // row background, so pressing one started a band and cleared
            // everything — and those gaps are most of the width of both
            // columns, because the text is short. Explorer drags the whole row.
            //
            // Only when selected, because that is what makes the two readings
            // of the same pixel unambiguous: on something picked out it means
            // "take these", and on something not it means "start again here".
            if (row.IsSelected) return null;

            // Otherwise a band, but only where the row spans the list, because
            // then there is no empty space beside it and the background is the
            // only place left to start one. A tile leaves gaps of its own, and
            // stealing its background would make dragging a file out of the
            // grid needlessly fiddly.
            return row.Bounds.Width >= found.Bounds.Width * 0.9 ? found : null;
        }

        return null;
    }

    /// <summary>
    /// The class the three row templates put on a selection box, and the only
    /// thing tying the markup to the handler that gives it meaning.
    /// </summary>
    internal const string SelectionBoxClass = "pick";

    /// <summary>
    /// The class the three row templates put on the box a name is typed in.
    /// The style that sizes it to a row is keyed off this, and so is every test
    /// that has to find an editor which exists only inside a DataTemplate.
    /// </summary>
    internal const string RenameBoxClass = "rename";

    /// <summary>
    /// The selection box a press landed on, or null for a press anywhere else.
    ///
    /// **The box cannot be a CheckBox, so nothing toggles unless this is
    /// asked.** A press inside a row is claimed on the window's tunnel, which
    /// runs before the ListBox does — and that is not an optimisation, it is
    /// the only place the press can be taken: by the time the ListBox has seen
    /// it, a five-file selection is already one file, which is measured and
    /// exactly what the boxes exist to avoid. Handling it there also stops a
    /// CheckBox's own class handler from ever running, which is why the box in
    /// the markup is a drawn Border rather than a control.
    ///
    /// Stops at the ListBoxItem, so the walk ends inside the row it started in
    /// and cannot wander up into some other list's box.
    /// </summary>
    internal static Control? SelectionBoxAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem) return null;

            if (visual is Control box && box.Classes.Contains(SelectionBoxClass)) return box;
        }

        return null;
    }

    /// <summary>
    /// The class the details row template puts on its expand triangle, and the
    /// only thing tying that markup to the handler that gives it meaning.
    /// </summary>
    internal const string ExpanderClass = "twist";

    /// <summary>
    /// The heading drawn over the first row of a group. Marked so
    /// <see cref="EntryAt"/> can tell it from the row it is drawn inside — see
    /// there for why that difference matters.
    /// </summary>
    internal const string GroupHeadingClass = "groupheading";

    /// <summary>
    /// The expand triangle a press landed on, or null for a press anywhere
    /// else.
    ///
    /// Same walk and the same reason as <see cref="SelectionBoxAt"/>: the press
    /// has to be claimed on the window's tunnel, before the ListBox reads it as
    /// a click on the row and collapses a multiple selection down to the one
    /// under the pointer. Stops at the ListBoxItem so the walk ends inside the
    /// row it started in.
    ///
    /// That stop has NO KILLING MUTATION, and it was looked for: turning it
    /// into a stop at the ListBox left the whole Vaktari.Ui.Tests project
    /// green, because nothing above a row in this window carries the class and
    /// the walk therefore runs out either way. It stays because the pane it
    /// would otherwise walk out of is the one whose triangle the press belongs
    /// to — the same bound SelectionBoxAt draws.
    /// </summary>
    internal static Control? ExpanderAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem) return null;

            if (visual is Control cell && cell.Classes.Contains(ExpanderClass)) return cell;
        }

        return null;
    }

    /// <summary>
    /// Opens the folder the keyboard is on without leaving this one, or shuts
    /// it again — and says whether it did anything.
    ///
    /// **False is the answer that gives the key back.** Left and Right already
    /// mean something in the grid and compact layouts, where the wrap panel
    /// moves the selection sideways with them, so a key claimed here whenever
    /// it was pressed would take that away. It is claimed only for the press
    /// that actually turns a triangle: the right key, on a folder, in the one
    /// layout that draws them, in a state the press would change.
    /// </summary>
    internal static bool TurnExpansion(ViewModels.PaneViewModel pane, bool open)
    {
        if (!pane.IsDetailsView || !pane.CanExpandRows) return false;

        if (pane.SelectedEntry is not { IsDirectory: true } row) return false;

        // Already the way it was asked to be. Right on an open folder is a
        // keystroke Dolphin spends moving into the first child; here it is left
        // alone rather than given a second meaning nothing announces.
        if (pane.IsExpanded(row.FullPath) == open) return false;

        _ = pane.ToggleExpandAsync(row);

        return true;
    }

    /// <summary>The list a press landed in, row or background alike.</summary>
    private static ListBox? ListingAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is ListBox list) return list;
        }

        return null;
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
    /// What clicking the heading's box means, given what is currently chosen.
    ///
    /// **A three-state CheckBox cycles unchecked, checked, indeterminate on
    /// click**, so the box's own value after a click is not an answer anybody
    /// meant to give — clicking a full listing would ask for "some". The
    /// question is asked of the PANE instead: everything ticked means clear,
    /// and anything else means tick the lot. That also makes a half-ticked
    /// listing go to all rather than to none, which is what both references do.
    /// </summary>
    internal static bool SelectAllFrom(bool? chosen) => chosen != true;

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
                _ = side is Input.SideButtonAction.Back
                    ? pane.GoUpAsync()
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
    /// Base metrics at scale 1.0. Everything in the markup is a DynamicResource
    /// pointing at these, so re-writing them here restyles the whole window
    /// without touching a single control.
    /// </summary>


    /// <summary>
    /// Text and icons scale on separate axes; everything structural is derived
    /// from whichever of the two drives it. A row has to fit the taller of its
    /// label and its thumbnail, so its height cannot be a third free setting —
    /// it would only ever be set wrong.
    /// </summary>
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
    /// Non-modal on purpose: you frequently want to compare two files, and a
    /// modal dialog makes that impossible without closing it first.
    /// </summary>
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
            _services.Recents);

        // The pane already holds the detected list, ordered and cached, so the
        // dialog borrows it rather than probing the disk again as it opens.
        if (_shell.ActiveTab is { } pane) model.UseTerminals(pane.Terminals);

        var window = new SettingsWindow(model);

        // The dialogs belong to the window. A view model that opens a folder
        // picker cannot be constructed in a test, and this one already is.
        /// <summary>A folder for a picker to open at, or null where there is
        /// nothing there yet and the picker should choose for itself.</summary>
        static async Task<Avalonia.Platform.Storage.IStorageFolder?> Suggested(
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

            // Here rather than in the dialog, so it lands through the one
            // handler that already applies a save — and so Cancel throws it
            // away like every other change made on those six pages.
            if (model.ForgetViewsOnSave) _services.FolderViews.ForgetAll();
            if (model.ForgetRecentOnSave) _services.Recents.ForgetAll();

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
            _shell.OnSettingsChanged();

            _fullPathInTitle = model.Result.Startup.ShowFullPathInTitleBar;
            RefreshTitle();
        };

        window.ShowDialog(this);
    }

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

    /// <summary>
    /// What a confirmation is about: the multi-selection, or the one focused
    /// row when nothing is properly selected. Both prompts read it the same
    /// way, and the count they used to print was derived from exactly this.
    /// </summary>
    private static IReadOnlyList<FileEntry> Chosen(PaneViewModel pane)
        => pane.Selection.Count > 0
            ? pane.Selection.ToList()
            : pane.SelectedEntry is { } one ? [one] : [];

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

    /// <summary>
    /// Builds the desktop's own menu, at the moment its submenu opens.
    ///
    /// Not on a binding: building it gives every shell extension on the machine
    /// a turn, and no ordinary right-click should pay for something behind one
    /// more hover.
    /// </summary>
    private void OnShellMenuOpening(object? sender, RoutedEventArgs e)
    {
        // **SubmenuOpened BUBBLES, and the shell's menu nests.** Hovering
        // 7-Zip's own submenu raised this event again on the way up, and
        // handling it rebuilt the whole menu — whose first act is to clear the
        // collection the open popup was being drawn from. The submenu appeared
        // and vanished in the same instant, every time, for every extension
        // that cascades: Send to, VLC, Restore previous versions.
        //
        // Only this item's own opening counts.
        if (!ReferenceEquals(e.Source, sender)) return;

        if (sender is Control { DataContext: PaneGroupViewModel { ActiveTab: { } pane } })
            _ = pane.OpenShellMenuAsync();
    }

    /// <summary>
    /// Releases it when the menu closes.
    ///
    /// **The ids are offsets into one live menu**, so they are meaningless once
    /// it is gone — and each menu owns an STA thread, so never releasing would
    /// leak one per right-click.
    /// </summary>
    /// <summary>
    /// Shows whichever Proton entries apply to the item under the menu.
    ///
    /// Decided here rather than bound, because the questions are per-item and
    /// per-machine at once: is the CLI installed, is the path inside the drive
    /// folder, and did Vaktari already make a link for it. Three hidden items
    /// cost nothing when the answer is no.
    /// </summary>
    private void OnListingMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        // **Re-read on every menu open, which is what their own comments always
        // claimed.** Both were called once, from the pane's constructor, so
        // adding a script or a template needed a restart to appear — while the
        // menu itself invites you to go and add one ("Add your own scripts"
        // opens the folder) and then never notices what you put there. Reading
        // two small directories is cheap next to building this menu at all.
        if (menu.DataContext is ViewModels.PaneGroupViewModel { ActiveTab: { } tab })
        {
            tab.RefreshScripts();
            tab.RefreshTemplates();

            // The Undo row names what it will take back, and the history is
            // the engine's — shared by every pane — so it is read when the
            // menu opens rather than tracked here.
            tab.RefreshUndoState();

            // Not awaited: a menu opens now. The Paste row shows what the last
            // answer was and corrects itself a round trip later. Deliberately
            // in THIS block, above the early return further down that has
            // silently swallowed work in this handler before.
            _ = tab.RefreshClipboardAsync();
        }

        // The Proton entries live inside the Share submenu now, so the walk
        // has to descend — and it has to see more than MenuItems, because the
        // rule between the two sharing methods is a Separator. The first
        // version walked OfType<MenuItem> only, could never find it, and the
        // early return below silently kept the whole submenu hidden: an eye
        // test on a machine WITH copyparty is what caught it.
        static T? Find<T>(IEnumerable<object?> items, string name) where T : Control
        {
            foreach (var item in items.OfType<Control>())
            {
                if (item is T match && match.Name == name) return match;
                if (item is MenuItem parent && Find<T>(parent.Items, name) is { } nested)
                    return nested;
            }

            return null;
        }

        if (Find<MenuItem>(menu.Items, "ShareMenu") is not { } shareMenu
            || Find<MenuItem>(menu.Items, "ProtonShareItem") is not { } share
            || Find<MenuItem>(menu.Items, "ProtonCopyLinkItem") is not { } copy
            || Find<MenuItem>(menu.Items, "ProtonUnshareItem") is not { } unshare
            || Find<MenuItem>(menu.Items, "ProtonInstallingItem") is not { } installing
            || Find<Separator>(menu.Items, "ShareMethodSeparator") is not { } separatorHost) return;

        var entry = (menu.DataContext as ViewModels.PaneGroupViewModel)?.ActiveTab?.SelectedEntry;
        var path = entry?.FullPath;

        // Linkable is about WHERE the item is, not whether the tool exists —
        // the share click installs what is missing. The busy row takes the
        // share row's seat while that download runs.
        var linkable = path is not null && _shell.CanLinkShare(path);
        var existing = path is not null ? _shell.LinkFor(path) : null;
        var busy = path is not null && _shell.ShowDriveInstallBusy(path);

        share.IsVisible = linkable && existing is null && !busy;
        copy.IsVisible = linkable && existing is not null;
        unshare.IsVisible = linkable && existing is not null;
        installing.IsVisible = busy;

        // The submenu earns its place when EITHER way of sharing applies; the
        // rule between them only when both do.
        shareMenu.IsVisible = linkable || _shell.HasSharingEntry;
        separatorHost.IsVisible = linkable && _shell.HasSharingEntry;
    }

    private void OnProtonShareClicked(object? sender, RoutedEventArgs e)
    {
        if (PaneFromMenuItem(sender)?.SelectedEntry is { } entry)
            _ = _shell.CreateDriveLinkAsync(entry.FullPath);
    }

    private void OnProtonCopyLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (PaneFromMenuItem(sender)?.SelectedEntry is { } entry
            && _shell.LinkFor(entry.FullPath) is { } link)
            _shell.CopyDriveLinkCommand.Execute(link);
    }

    private void OnProtonUnshareClicked(object? sender, RoutedEventArgs e)
    {
        if (PaneFromMenuItem(sender)?.SelectedEntry is { } entry
            && _shell.LinkFor(entry.FullPath) is { } link)
            _shell.StopDriveLinkCommand.Execute(link);
    }

    /// <summary>
    /// The pane whose menu the clicked item belongs to — read from the item's
    /// own DataContext, which it inherits from the ContextMenu.
    ///
    /// **Not through Parent**, which this used to do: when the Proton rows
    /// moved inside the Share submenu, their Parent became that submenu rather
    /// than the ContextMenu, the cast answered null, and every click on them
    /// did nothing without a word said. Inheritance does not care how deep the
    /// row sits.
    /// </summary>
    private static ViewModels.PaneViewModel? PaneFromMenuItem(object? sender)
        => (sender as Control)?.DataContext is ViewModels.PaneGroupViewModel group
            ? group.ActiveTab
            : null;

    private void OnListingMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: PaneGroupViewModel { ActiveTab: { } pane } })
            return;

        pane.CloseShellMenu();

        // Disarmed with the menu. Left set, the next ordinary right-click would
        // still be offering elevation because of a Shift held a minute ago.
        pane.AdminRequested = false;
    }

    /// <summary>Feeds the pane its own width so columns can drop out in
    /// priority order rather than being squeezed.</summary>
    private void OnListSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control { DataContext: PaneViewModel pane })
            pane.ViewportWidth = e.NewSize.Width;
    }

    // ---- drag and drop -------------------------------------------------

    // ---- rubber-band selection --------------------------------------------

    /// <summary>The list a band is being drawn in, or null when none is.</summary>
    private ListBox? _bandList;

    /// <summary>Where the drag began, in the overlay's coordinates.</summary>
    private Point _bandOrigin;

    /// <summary>The band as last drawn, so the scroll timer can re-test against
    /// it without a pointer event.</summary>
    private Rect _bandRect;

    /// <summary>Ctrl or Shift was held, so the band ADDS to the selection.</summary>
    private bool _bandAdditive;

    /// <summary>
    /// The selection as it stood when an additive band began.
    ///
    /// Snapshotted rather than read live, because the band rewrites the
    /// selection on every move — without a fixed baseline, shrinking the
    /// rectangle could not give back what it had already taken.
    /// Null until the band actually starts, so a click that never moves leaves
    /// the selection alone.
    /// </summary>
    private List<object>? _bandKept;

    /// <summary>
    /// The scroll offset when the band started.
    ///
    /// **The origin was anchored to the window**, so auto-scrolling slid the
    /// content out from under a rectangle that stayed put: the band never
    /// covered more than one screenful, and everything swept past scrolled out
    /// of it. Holding the offset lets the rectangle be re-expressed in content
    /// terms every pass, so it keeps covering everything the drag has crossed.
    /// </summary>
    private Vector _bandScrollAt;

    /// <summary>
    /// What the band has selected so far.
    ///
    /// A row that has scrolled out of the viewport has no container and no
    /// bounds, so it cannot be re-tested — and rebuilding the selection from
    /// what is visible therefore DESELECTED it. Marquee-selecting two hundred
    /// files kept only the last screenful. Rows that have left stay selected;
    /// rows still on screen are re-tested as before, so dragging back up still
    /// takes them off.
    /// </summary>
    private readonly List<object> _bandTaken = [];

    private Point _dragOrigin;

    private PaneViewModel? _tabDrag;
    private Avalonia.Controls.Primitives.TabStrip? _tabStrip;
    private double _tabGrab;
    private bool _tabDragging;

    private ViewModels.PlaceItemViewModel? _placeDrag;
    private ItemsControl? _placeList;
    private double _placeGrab;
    private bool _placeDragging;

    private PaneViewModel? _dragSource;
    private bool _dragging;

    /// <summary>
    /// What was selected at the moment the button went down.
    ///
    /// **Because pressing collapses the selection before the drag starts.**
    /// Select five files, press on one of them and drag: the press has already
    /// reduced the selection to that row by the time BeginDragAsync reads it,
    /// so four files were silently left behind. The right-button drag carried
    /// all five, because Avalonia treats a right press differently — which is
    /// what made it look like a listing quirk rather than a drag one.
    /// </summary>
    private List<string>? _dragSelection;

    // The press that began the gesture, held until the move threshold is
    // crossed.
    //
    // This looks like retaining event args past their handler, and it is — but
    // DragDrop.DoDragDropAsync takes PointerPressedEventArgs specifically, not
    // the PointerEventArgs the move handler receives, so a drag cannot be
    // started from the move without it. Starting from the press instead would
    // mean no movement threshold, and every click on a row would begin a drag.
    // The alternative is worse than the constraint.
    private PointerPressedEventArgs? _dragTrigger;

    /// <summary>
    /// True while a drag that started inside Vaktari is in flight. Dragging within
    /// a file manager conventionally means move; dragging in from another
    /// application means copy. Ctrl and Shift override either way.
    /// </summary>
    private bool _internalDrag;

    /// <summary>Walks up from whatever was hit to the pane that owns it.</summary>
    private static PaneViewModel? PaneAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PaneViewModel pane }) return pane;
        }

        return null;
    }

    /// <summary>
    /// The tab under the pointer, or null if the pointer is not on the strip.
    ///
    /// **Not PaneAt.** A tab and a listing both carry a PaneViewModel as their
    /// data context — that is the whole point of the tab strip — so telling
    /// them apart has to be done by container, or a middle-click in the listing
    /// would close the tab it was aimed into.
    /// </summary>
    private static PaneViewModel? TabAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Avalonia.Controls.Primitives.TabStripItem { DataContext: PaneViewModel pane })
                return pane;
        }

        return null;
    }

    /// <summary>
    /// The group whose tab strip a gesture landed on, and only where the strip
    /// is BLANK — not on a tab, the "+", a chevron or the scrollbar.
    ///
    /// **The empty half of the strip answered nothing.** Explorer, Dolphin and
    /// every browser open a tab when it is double-clicked, and here the gesture
    /// fell through to the row walk, found no file and stopped — with the "+"
    /// itself scrolled out of reach behind a dozen tabs, which is exactly when
    /// the blank space is aimed at.
    ///
    /// Keyed on the strip's own ScrollViewer rather than the tab bar behind it,
    /// so the layout buttons docked to its right — and the gaps between them —
    /// keep meaning nothing. Button covers the "+", a tab's ✕ and both overflow
    /// chevrons in one test, because RepeatButton and ToggleButton both derive
    /// from it; the scrollbar and its thumb are refused for the reason
    /// <see cref="ListForEmptySpace"/> refuses them, which is that scrolling is
    /// not an opening gesture.
    /// </summary>
    private static PaneGroupViewModel? TabStripEmptySpaceAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Avalonia.Controls.Primitives.TabStripItem
                       or Button
                       or Avalonia.Controls.Primitives.ScrollBar
                       or Avalonia.Controls.Primitives.Thumb)
                return null;

            if (visual is ScrollViewer { DataContext: PaneGroupViewModel group } strip
                && strip.Classes.Contains("tab-space"))
                return group;
        }

        return null;
    }

    /// <summary>Walks up from whatever was hit to the group that owns it.</summary>
    private static PaneGroupViewModel? GroupAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PaneGroupViewModel group }) return group;
        }

        return null;
    }

    /// <summary>
    /// Which pane a navigation button moves, for a press that landed anywhere.
    ///
    /// **The side buttons navigated a tab nobody could see.** A tab header
    /// carries its own pane as its data context — that is how the strip is
    /// bound, and it is what lets a middle click close the tab under the
    /// pointer — so walking up from the press answered with the tab that was
    /// pointed AT rather than the listing on screen. Pressing back while aiming
    /// at the third tab's label rewound the third tab: the visible listing did
    /// not move, nothing said anything, and the only trace was a title quietly
    /// changing on a folder that was not open. Every browser drives the page
    /// you are looking at, whichever piece of chrome the pointer is over.
    ///
    /// The strip belongs to its group, so the group's own active tab is the
    /// answer — in a split, pointing at one side's tabs still navigates that
    /// side, which is the pane-under-the-pointer rule the rest of this handler
    /// follows rather than an exception to it. It also fixes the strip's
    /// background and its + button, which reached no pane at all and fell
    /// through to the OTHER side's active tab.
    /// </summary>
    private static PaneViewModel? NavigationTargetAt(object? source)
    {
        var pane = TabAt(source) is null ? PaneAt(source) : null;

        return pane ?? GroupAt(source)?.ActiveTab;
    }

    /// <summary>
    /// Notes that a press landed on a tab, so a move past the threshold can
    /// reorder the strip.
    ///
    /// **Tabs could not be reordered at all** — the press was recorded and then
    /// dropped, because neither of the things a press arms is a tab: EntryAt
    /// finds no row, and ListForEmptySpace bails on the tab template. This must
    /// leave both of those alone; a tab that armed <c>_dragSource</c> would drag
    /// real files again, which is the fault the ROW rule above was written to
    /// fix.
    ///
    /// Not the DragDrop system, deliberately: the strip already declares
    /// AllowDrop so a tab is a target for FILE drops, and starting a real drag
    /// from a tab would put a drop target and a reorder on one gesture.
    ///
    /// The close button lives inside the item, so a press on it reaches here
    /// too — a wobble while pressing ✕ should close the tab, not shuffle the
    /// strip first.
    /// </summary>
    private void ArmTabDrag(PointerPressedEventArgs e, PointerPointProperties properties)
    {
        EndTabDrag();

        if (!properties.IsLeftButtonPressed) return;

        for (var visual = e.Source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Button) return;

            if (visual is not Avalonia.Controls.Primitives.TabStripItem
                { DataContext: PaneViewModel tab } item) continue;

            if (item.FindAncestorOfType<Avalonia.Controls.Primitives.TabStrip>() is not { } strip)
                return;

            _tabDrag = tab;
            _tabStrip = strip;

            // Where inside the tab it was grabbed, so the tab keeps its grip on
            // the pointer however the strip scrolls underneath.
            _tabGrab = e.GetPosition(item).X;

            return;
        }
    }

    /// <summary>
    /// Moves the pressed tab under the pointer.
    ///
    /// Container bounds rather than accumulated widths: margins and the strip's
    /// own padding are not this function's business, and TranslatePoint is exact
    /// whatever the panel does with them. A container that is not laid out yet
    /// gives no geometry to reason about, so the frame is skipped rather than
    /// computed from zeroes — the strip would shuffle at random.
    /// </summary>
    private void DragTab(PointerEventArgs e)
    {
        if (_tabDrag is not { } tab
            || _tabStrip is not { DataContext: PaneGroupViewModel group })
        {
            EndTabDrag();
            return;
        }

        // The button can be released outside the window, where no release
        // arrives — the live state ends the drag, not just the event that ought
        // to have come. The band already works this way.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndTabDrag();
            return;
        }

        var here = e.GetPosition(this);

        // X only: the strip is one horizontal row, and a vertical wobble while
        // pressing a tab is not a reorder.
        if (!_tabDragging && Math.Abs(here.X - _dragOrigin.X) < 6) return;

        _tabDragging = true;

        var from = group.Tabs.IndexOf(tab);
        if (from < 0)
        {
            EndTabDrag();
            return;
        }

        var middles = new List<double>(group.Tabs.Count);
        double width = 0;

        for (var i = 0; i < group.Tabs.Count; i++)
        {
            if (_tabStrip.ContainerFromIndex(i) is not Control box
                || box.TranslatePoint(default, this) is not { } at) return;

            middles.Add(at.X + box.Bounds.Width / 2);

            if (i == from) width = box.Bounds.Width;
        }

        group.MoveTab(tab, DragReorder.SlotFor(here.X - _tabGrab + width / 2, middles, from));
    }

    private void EndTabDrag()
    {
        _tabDrag = null;
        _tabStrip = null;
        _tabDragging = false;
    }

    /// <summary>
    /// Arms a reorder of the sidebar's pinned places.
    ///
    /// **Both providers have implemented ReorderAsync since they were written
    /// and nothing ever called it.** Pins stayed in the order they were added,
    /// and the only way to change that was to edit places.json by hand — which
    /// starts to matter at exactly the point a sidebar has enough pins to be
    /// worth tidying. Explorer and Dolphin both reorder by dragging.
    ///
    /// Unlike the tab strip's version this does NOT stop at a Button, because
    /// the place row IS one. The pinned test is what keeps the gesture off the
    /// rows it must not move.
    /// </summary>
    private void ArmPlaceDrag(PointerPressedEventArgs e, PointerPointProperties properties)
    {
        EndPlaceDrag(save: false);

        if (!properties.IsLeftButtonPressed) return;

        if (e.Source is not Visual source) return;
        if (PlaceDrag.ArmedBy(source) is not { } place) return;
        if (PlaceDrag.ListFor(source) is not { } list) return;

        _placeDrag = place;
        _placeList = list;

        // Where inside the row it was grabbed, so the row keeps its grip on the
        // pointer whatever the list does underneath.
        _placeGrab = e.GetPosition(list.ContainerFromItem(place) as Visual ?? source).Y;
    }

    /// <summary>
    /// Moves the pressed place under the pointer.
    ///
    /// The Y twin of <see cref="DragTab"/>, over the same arithmetic and for
    /// the same reason — a neighbour's MIDDLE rather than its near edge, which
    /// is what stops a tall row dragged past a short one from oscillating every
    /// frame.
    ///
    /// Only the pinned rows are candidates, so their centres are what the
    /// pointer is compared against: a pin dragged to the top of the list lands
    /// at the top of the PINS rather than above Home.
    /// </summary>
    private void DragPlace(PointerEventArgs e)
    {
        if (_placeDrag is not { } place
            || _placeList is not { DataContext: ViewModels.PlaceGroupViewModel group })
        {
            EndPlaceDrag(save: false);
            return;
        }

        // The button can be released outside the window, where no release
        // arrives — the live state ends the drag, not just the event that ought
        // to have come.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndPlaceDrag(save: true);
            return;
        }

        var here = e.GetPosition(this);

        // Y only: the sidebar is one vertical column, and a horizontal wobble
        // while pressing a place is not a reorder.
        if (!_placeDragging && Math.Abs(here.Y - _dragOrigin.Y) < 6) return;

        _placeDragging = true;

        var rows = group.PinnedRows();
        var from = rows.IndexOf(group.Places.IndexOf(place));

        if (from < 0)
        {
            EndPlaceDrag(save: false);
            return;
        }

        var middles = new List<double>(rows.Count);
        double height = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            if (_placeList.ContainerFromIndex(rows[i]) is not Control box
                || box.TranslatePoint(default, this) is not { } at) return;

            middles.Add(at.Y + box.Bounds.Height / 2);

            if (i == from) height = box.Bounds.Height;
        }

        group.MovePin(place, DragReorder.SlotFor(here.Y - _placeGrab + height / 2, middles, from));
    }

    /// <summary>
    /// Ends the reorder, writing the new order down only if one really
    /// happened — a plain click on a pinned place arms this and moves nothing,
    /// and must not send the provider a write on every click.
    /// </summary>
    /// <returns>Whether a reorder really happened, which the release handler
    /// uses to keep the drop from also being a click.</returns>
    private bool EndPlaceDrag(bool save)
    {
        var moved = save && _placeDragging;

        _placeDrag = null;
        _placeList = null;
        _placeDragging = false;

        if (moved) _ = _shell.Sidebar.SavePinOrderAsync();

        return moved;
    }

    /// <summary>
    /// The sidebar place under the pointer, if a drop should go into it.
    ///
    /// Explorer takes a drop on the tree and on Quick access, and dragging a
    /// file onto Downloads or a drive is how a good deal of filing gets done.
    /// The sidebar accepted nothing, so the drag died over it with no cursor
    /// and no explanation.
    ///
    /// Only where the place is a real folder: a share that is not mounted has
    /// nowhere to put anything, and its own row already says so.
    /// </summary>
    private static string? PlaceAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PlaceItemViewModel { IsAvailable: true } place }
                && place.Path.Length > 0
                && !VirtualPaths.IsVirtual(place.Path))
                return place.Path;
        }

        return null;
    }

    /// <summary>
    /// Whether the pointer is over the bin's row.
    ///
    /// Separate from <see cref="PlaceAt"/>, which deliberately refuses a
    /// virtual path because those are not folders anything can be copied into.
    /// The bin is the one virtual place that IS a destination — for exactly one
    /// verb.
    /// </summary>
    private static bool TrashRowAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PlaceItemViewModel place }
                && PathRules.Same(place.Path, VirtualPaths.Trash))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Everything a drag needs to know about what is under the pointer.
    ///
    /// **Drag-over and drop used to work this out separately, and disagreed.**
    /// OnDrop had mapped the bin's row to Trash; OnDragOver never considered
    /// it, so the cursor showed no-drop and the toolkit — which delivers a drop
    /// only where the drag-over said yes — never delivered one. The branch in
    /// OnDrop was unreachable code that read like a working feature. One
    /// answer, read by both handlers, is the only arrangement in which they
    /// cannot drift apart again.
    /// </summary>
    private readonly record struct DropTarget(
        bool IsBin, string? Place, string? Crumb, string? Folder, PaneViewModel? Pane)
    {
        /// <summary>Somewhere a drop could land. False refuses the drag.</summary>
        public bool Exists => IsBin || Place is not null || Crumb is not null || Pane is not null;

        /// <summary>
        /// The folder a drop goes into. Empty for the bin, which is a verb
        /// rather than a place to put things.
        ///
        /// A crumb outranks the pane below it and is outranked by a sidebar
        /// place, which is the order the pointer is actually over them in.
        /// </summary>
        public string Destination => Explicit ?? Pane?.CurrentPath ?? "";

        /// <summary>
        /// A folder the pointer is over in its own right — a place, a crumb or
        /// a folder row — as opposed to falling back to the folder being
        /// listed. The right-button drop menu needs the difference: it offers
        /// to put things INTO what you pointed at, and pointing at nothing in
        /// particular is not the same as pointing at the current folder.
        /// </summary>
        public string? Explicit => Place ?? Crumb ?? Folder;
    }

    private static DropTarget TargetAt(object? source) => new(
        TrashRowAt(source), PlaceAt(source), CrumbAt(source),
        FolderRowAt(source), PaneAt(source));

    /// <summary>
    /// The breadcrumb segment under the pointer.
    ///
    /// **Dragging onto an ancestor did nothing**, though it is the shortest way
    /// to move something up two levels and both Explorer and Dolphin take it.
    /// The crumbs sit above the listing, so a drag over them found the pane and
    /// offered the pane's own folder — the drop went where the file already
    /// was, which is a no-op that looks like a bug.
    /// </summary>
    private static string? CrumbAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PathSegment crumb }
                && crumb.FullPath.Length > 0
                && !VirtualPaths.IsVirtual(crumb.FullPath))
                return crumb.FullPath;
        }

        return null;
    }

    /// <summary>
    /// The sidebar row under the pointer, whatever it names.
    ///
    /// **Not <see cref="PlaceAt"/>, which answers a DROP.** That one refuses a
    /// virtual path, because the bin and the two recent listings are not
    /// folders anything can be copied into, and it refuses a share that is not
    /// mounted. Opening is the other question: the row's own menu offers "Open
    /// in new tab" on every row it draws, and a gesture that answers fewer rows
    /// than the menu beside it reads as broken rather than as careful.
    /// </summary>
    private static string? PlaceRowAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PlaceItemViewModel { Path.Length: > 0 } place })
                return place.Path;
        }

        return null;
    }

    /// <summary>
    /// The crumb under the pointer, virtual ones included — This PC is somewhere
    /// you can be even though it is nowhere a file can land, which is the only
    /// reason <see cref="CrumbAt"/> refuses it.
    /// </summary>
    private static string? CrumbRowAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PathSegment { FullPath.Length: > 0 } crumb })
                return crumb.FullPath;
        }

        return null;
    }

    /// <summary>
    /// The folder a press asks for in a NEW tab, or null when it asks for
    /// nothing of the kind.
    ///
    /// **A sidebar place and a breadcrumb answered neither gesture.** Middle
    /// click reached only the tab strip and a folder row, and a Button ignores
    /// a middle press altogether — so middle-clicking Documents in the sidebar,
    /// or an ancestor in the path bar, did nothing at all, while the row's own
    /// right-click menu offered "Open in new tab" and the F1 sheet advertised
    /// the middle button. Both references open a tab from both.
    ///
    /// Ctrl+click is offered on the sidebar and the crumbs only. In the listing
    /// that gesture already means "add this row to the selection", and taking
    /// it would break the most-used modifier in the application to add a second
    /// way of doing something the middle button already does.
    ///
    /// Place, then crumb, then folder row — the same order
    /// <c>DropTarget.Explicit</c> reads them in, and the order the pointer is
    /// physically over them in.
    /// </summary>
    private static string? NewTabTarget(
        object? source, PointerUpdateKind kind, KeyModifiers modifiers)
    {
        var middle = kind is PointerUpdateKind.MiddleButtonPressed;

        // Ctrl+middle is the pane-scale reset and keeps its own meaning. The
        // handler returns on it long before this; saying so here is what lets
        // the rule be read and tested on its own.
        if (middle && modifiers.HasFlag(KeyModifiers.Control)) return null;

        var ctrlLeft = kind is PointerUpdateKind.LeftButtonPressed
                       && modifiers == KeyModifiers.Control;

        if (!middle && !ctrlLeft) return null;

        if (PlaceRowAt(source) is { } place) return place;
        if (CrumbRowAt(source) is { } crumb) return crumb;

        // The listing keeps Ctrl+click for extending a selection.
        return middle ? FolderRowAt(source) : null;
    }

    /// <summary>The folder row under the pointer, if the drop should go into it
    /// rather than into the directory being listed.</summary>
    private static string? FolderRowAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: FileEntry { IsDirectory: true } entry })
                return entry.FullPath;
        }

        return null;
    }

    /// <summary>
    /// Accumulated wheel travel. A mouse notch is a whole 1.0, but a trackpad
    /// sends a stream of fractions — stepping on each one would race from
    /// smallest to largest in a single swipe.
    /// </summary>
    private double _zoomTravel;

    /// <summary>
    /// A strip that scrolls sideways and not down takes the wheel.
    ///
    /// **The toolkit does not do this, and the tab strip proved it.** A vertical
    /// wheel over the tabs did nothing whatever: the ScrollViewer there has its
    /// vertical axis disabled, so there was no vertical scrolling to perform and
    /// the horizontal axis was never offered the gesture. What made that
    /// survivable was the theme's stepper arrows — which are exactly the bulky
    /// machinery the strip has just stopped drawing. Removing them without this
    /// would have left dragging a three-pixel line as the only way to reach a
    /// tab that had scrolled out.
    ///
    /// Wheeling away from you goes right, which is the direction the same
    /// gesture moves a horizontal strip everywhere else.
    /// </summary>
    private static bool ScrolledSideways(PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return false;

        foreach (var visual in (e.Source as Visual)?.GetVisualAncestors()
                 ?? Array.Empty<Visual>())
        {
            if (visual is not ScrollViewer viewer) continue;
            if (viewer.VerticalScrollBarVisibility
                != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled) continue;

            // Nothing hidden means nothing to reach, and claiming the gesture
            // then would stop it doing whatever it would otherwise have done.
            if (viewer.Extent.Width <= viewer.Viewport.Width) return false;

            // A notch is 1.0 and moves about the width of a short tab. A
            // trackpad's fractions scale down from the same figure rather than
            // needing an accumulator, because a partial scroll is meaningful
            // where a partial zoom step is not.
            var moved = Math.Clamp(
                viewer.Offset.X - (e.Delta.Y * 64), 0, viewer.Extent.Width - viewer.Viewport.Width);

            viewer.Offset = viewer.Offset.WithX(moved);

            return true;
        }

        return false;
    }

    /// <summary>
    /// The tab ScrollViewer the pressed chevron flanks. By tree rather than by
    /// name, because the strip lives in a template stamped once per pane and a
    /// name would find only one of them.
    /// </summary>
    private static ScrollViewer? TabScrollerFor(object? sender)
    {
        for (var visual = sender as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is DockPanel dock)
                return dock.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        return null;
    }

    private void OnScrollTabsLeft(object? sender, RoutedEventArgs e) => NudgeTabs(sender, -1);

    private void OnScrollTabsRight(object? sender, RoutedEventArgs e) => NudgeTabs(sender, +1);

    private static void NudgeTabs(object? sender, int direction)
    {
        if (TabScrollerFor(sender) is not { } scroller) return;

        scroller.Offset = scroller.Offset.WithX(TabStripScroll.Toward(
            scroller.Offset.X, scroller.Viewport.Width, scroller.Extent.Width, direction));
    }

    /// <summary>
    /// Keeps the chevrons truthful. ScrollChanged fires for offset, extent and
    /// viewport alike, so opening a tab, closing one, resizing the window and
    /// scrolling all pass through here — there is no state to get stale.
    /// </summary>
    private void OnTabStripScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;

        DockPanel? dock = null;
        for (var visual = (Visual?)scroller; visual is not null; visual = visual.GetVisualParent())
            if (visual is DockPanel found) { dock = found; break; }

        if (dock is null) return;

        var overflows = TabStripScroll.Overflows(scroller.Extent.Width, scroller.Viewport.Width);

        foreach (var chevron in dock.GetVisualDescendants().OfType<RepeatButton>())
        {
            if (chevron.Classes.Contains("tab-nudge-left"))
            {
                chevron.IsVisible = overflows;
                chevron.IsEnabled = TabStripScroll.CanGoLeft(scroller.Offset.X);
            }
            else if (chevron.Classes.Contains("tab-nudge-right"))
            {
                chevron.IsVisible = overflows;
                chevron.IsEnabled = TabStripScroll.CanGoRight(
                    scroller.Offset.X, scroller.Viewport.Width, scroller.Extent.Width);
            }
        }
    }

    /// <summary>
    /// Scrolls the tab just selected into view. Ctrl+Tab can land on a tab the
    /// strip has scrolled past, and a selection you cannot see reads as the
    /// keystroke doing nothing. Posted, because at selection time the container
    /// may not have been arranged yet.
    /// </summary>
    private void OnTabStripSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Primitives.TabStrip strip
            || strip.SelectedItem is not { } selected) return;

        // Switching tabs changes the folder on screen as surely as navigating
        // does, and the title has to say so.
        RefreshTitle();

        Dispatcher.UIThread.Post(
            () => strip.ContainerFromItem(selected)?.BringIntoView(),
            DispatcherPriority.Loaded);
    }

    private void OnWheelAnywhere(object? sender, PointerWheelEventArgs e)
    {
        // Before the zoom test, because a strip that scrolls sideways wants the
        // plain wheel and zoom wants it only with Control held.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && ScrolledSideways(e))
        {
            e.Handled = true;
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;

        // Claimed even when the accumulator has not tripped yet: releasing it
        // would scroll the list mid-zoom.
        e.Handled = true;

        // Direction reversal starts over, so a small overshoot does not need to
        // be unwound before the other direction responds.
        if (Math.Sign(e.Delta.Y) != Math.Sign(_zoomTravel)) _zoomTravel = 0;

        _zoomTravel += e.Delta.Y;

        while (Math.Abs(_zoomTravel) >= 1.0)
        {
            var up = _zoomTravel > 0;
            _zoomTravel -= up ? 1.0 : -1.0;

            // The pane under the pointer, not the active one: reaching over to
            // scale the other side without clicking into it first is the whole
            // reason the wheel gesture is nicer than the buttons.
            var pane = PaneAt(e.Source) ?? _shell.ActiveTab;

            // Shift narrows it to the icons, which is the axis people mean when
            // they say "zoom" — the labels usually want to stay put.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                _shell.ScalePane(pane, 0, up ? 0.15 : -0.15);
            else
                _shell.ScalePane(pane, up ? 0.1 : -0.1, up ? 0.15 : -0.15);
        }
    }

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

    private void UpdateBand(PointerEventArgs e)
    {
        if (_bandList is not ListBox list) return;

        // The button can be released outside the window, where no release event
        // arrives — so the live button state is what ends the band, not just the
        // event that ought to have come.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndBand();
            return;
        }

        var here = e.GetPosition(BandLayer);

        // **The origin travels with the content.** It was fixed in window
        // coordinates, so once the list auto-scrolled the rectangle stopped
        // covering what the drag had crossed — the band could never select more
        // than one screenful. Subtracting how far the view has moved since the
        // press puts the anchor back over the row it started on.
        var scrolled = Scroller(list) is { } view ? view.Offset - _bandScrollAt : default;
        var anchor = new Point(_bandOrigin.X - scrolled.X, _bandOrigin.Y - scrolled.Y);

        var rect = new Rect(
            Math.Min(anchor.X, here.X), Math.Min(anchor.Y, here.Y),
            Math.Abs(here.X - anchor.X), Math.Abs(here.Y - anchor.Y));

        // The same six-pixel threshold the file drag uses. Below it this is a
        // click that happened to wobble, and rewriting the selection would make
        // clicking empty space feel unreliable.
        if (rect.Width < 6 && rect.Height < 6) return;

        _bandKept ??= _bandAdditive
            ? list.SelectedItems?.Cast<object>().ToList() ?? []
            : [];

        AutoScroll(list, here);

        Canvas.SetLeft(SelectionBand, rect.X);
        Canvas.SetTop(SelectionBand, rect.Y);
        SelectionBand.Width = rect.Width;
        SelectionBand.Height = rect.Height;
        SelectionBand.IsVisible = true;

        _bandRect = rect;

        ApplyBand(list, rect);
    }

    /// <summary>
    /// Selects every realized row the rectangle touches.
    ///
    /// **Realized rows only, and that is not a limitation to apologise for:** the
    /// listings virtualize, so a row outside the viewport has no container and no
    /// bounds. A band can only be drawn across what is on screen anyway.
    /// </summary>
    private void ApplyBand(ListBox list, Rect rect)
    {
        if (list.SelectedItems is not { } selected) return;

        var wanted = new List<object>(_bandKept ?? []);

        // **What the band already took, and can no longer see.** A row outside
        // the viewport has no container, so it cannot be re-tested — and
        // rebuilding from what is visible therefore dropped it. Sweeping two
        // hundred files kept only the last screenful.
        var realized = new List<object>();

        foreach (var container in Rows(list))
        {
            if (container.DataContext is not { } item) continue;

            realized.Add(item);

            // TranslatePoint rather than a stored offset: rows move as the list
            // scrolls, and a cached position would select the wrong ones the
            // moment it did.
            if (container.TranslatePoint(default, BandLayer) is not { } origin) continue;

            var bounds = new Rect(origin, container.Bounds.Size);

            if (bounds.Intersects(rect) && !wanted.Contains(item)) wanted.Add(item);
        }

        // Off-screen rows the band has already claimed stay claimed. On-screen
        // ones were just re-tested, so dragging back up still takes them off.
        foreach (var taken in _bandTaken)
            if (!realized.Contains(taken) && !wanted.Contains(taken))
                wanted.Add(taken);

        _bandTaken.Clear();

        foreach (var item in wanted)
            if (_bandKept is null || !_bandKept.Contains(item))
                _bandTaken.Add(item);

        // Diffed rather than cleared and refilled. Every change to this
        // collection refreshes the details panel and the status line, and a
        // clear-then-add would do that twice per pointer move.
        for (var i = selected.Count - 1; i >= 0; i--)
            if (selected[i] is { } existing && !wanted.Contains(existing))
                selected.RemoveAt(i);

        foreach (var item in wanted)
            if (!selected.Contains(item))
                selected.Add(item);
    }

    /// <summary>
    /// Every ListBoxItem beneath a control.
    ///
    /// Written with `GetVisualChildren` — the sibling of the `GetVisualParent`
    /// this file already relies on — rather than an items-control API whose shape
    /// varies between Avalonia versions.
    /// </summary>
    /// <summary>
    /// The listing the user is actually looking at: visible, showing the active
    /// tab, and able to hold more than one selection.
    ///
    /// Three layout lists exist per pane and all stay alive when hidden, so
    /// identity alone is not enough — `IsVisible` is what distinguishes them,
    /// and it is bound to the view mode.
    /// </summary>
    /// <summary>
    /// Pages the compact listing sideways.
    ///
    /// **On the TUNNEL phase, because something else was claiming these keys and
    /// doing nothing with them.** Compact disables vertical scrolling, so the
    /// ScrollViewer cannot act on PageUp/PageDown — yet mapping them inside the
    /// panel changed nothing, so the key was never reaching it. Tunnelling
    /// settles it without needing to know who was eating them: nothing
    /// downstream gets the chance.
    ///
    /// **Moves the VIEW, not the selection**, which is what Page already does in
    /// the grid — there the ScrollViewer pages the viewport and leaves the cursor
    /// where it was, and the user has said that feels right. Compact behaving
    /// differently would be the odd one out.
    /// </summary>
    private bool PageCompactListing(KeyEventArgs e)
    {
        if (e.Key is not (Key.PageUp or Key.PageDown)) return false;
        if (e.KeyModifiers != KeyModifiers.None) return false;

        // Never while typing — a path box or the rename prompt owns its own keys.
        if (FocusManager?.GetFocusedElement() is TextBox) return false;

        if (_shell.ActiveTab is not { View: ViewMode.Compact }) return false;
        if (ActiveListing() is not { } list || Scroller(list) is not { } scroller)
            return false;

        // A viewport less a sliver, so the column you were reading stays on
        // screen as an anchor rather than vanishing off the edge.
        var page = Math.Max(1, scroller.Viewport.Width - 48);
        var step = e.Key == Key.PageDown ? page : -page;

        var limit = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);

        scroller.Offset = scroller.Offset.WithX(
            Math.Clamp(scroller.Offset.X + step, 0, limit));

        // Claimed either way. At the end of the extent the key has still been
        // dealt with, and letting it fall through hands it back to whatever was
        // silently swallowing it before.
        e.Handled = true;
        return true;
    }

    /// <summary>
    /// Selects everything that is not selected, and deselects everything that
    /// is — Explorer's Ctrl+Shift+A, and the fastest way to say "all of these
    /// except those".
    ///
    /// Through the ListBox for the same reason SelectAll is: filling the bound
    /// collection row by row fires CollectionChanged once per file, and each
    /// one refreshes the details panel and recomputes the summary.
    /// </summary>
    private void InvertSelection()
    {
        if (ActiveListing() is not { } list) return;
        if (list.SelectedItems is not { } selected) return;

        var wanted = list.Items
            .OfType<object>()
            .Where(item => !selected.Contains(item))
            .ToList();

        selected.Clear();

        foreach (var item in wanted) selected.Add(item);
    }

    /// <summary>Clears the selection without touching what is focused.</summary>
    private void SelectNone() => ActiveListing()?.SelectedItems?.Clear();

    /// <summary>
    /// Opens the listing's context menu from the keyboard, at the focused row.
    ///
    /// The menu hangs off the ItemsControl that holds the tabs, so it is found
    /// by walking up from the listing rather than from the row — the row's own
    /// template has no menu of its own.
    /// </summary>
    private void OpenListingMenu()
    {
        if (ActiveListing() is not { } list) return;

        for (var visual = (Visual?)list; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is not Control { ContextMenu: { } menu }) continue;

            // Placed on the list rather than at the pointer: the pointer may be
            // anywhere, and the keyboard user is looking at the focused row.
            menu.Open(list);
            return;
        }
    }

    /// <summary>
    /// Ticks the FOLDER's rows, not the screen's.
    ///
    /// **A folder opened in place put rows from two folders in one listing**,
    /// and select-everything is the gesture that takes them both without being
    /// asked. Everything downstream is written for a selection that lives in
    /// ONE folder — read the three call sites: BatchRenameViewModel is handed
    /// pane.Entries as "the whole folder, so the preview can see the files that
    /// are already there", the rename run steps within one list, and the drop
    /// refusals ask whether a target is inside the selection. A copy of a
    /// folder together with a file inside it asks for that file at the
    /// destination twice — once inside the copied folder and once beside it.
    ///
    /// Ctrl+clicking across the boundary is still allowed — somebody who does
    /// that meant it — but the one keystroke that does it without being asked
    /// no longer does.
    ///
    /// The whole listing IS the folder until somebody presses a triangle, so
    /// the ordinary case still goes through the framework's bulk path: filling
    /// the bound collection row by row fires a change per file, and each one
    /// refreshes the details panel and recomputes the summary.
    /// </summary>
    internal static void SelectWholeFolder(ListBox list, ViewModels.PaneViewModel pane)
    {
        if (!pane.RowsAreSpliced) { list.SelectAll(); return; }

        list.SelectedItems?.Clear();

        foreach (var entry in pane.Entries) list.SelectedItems?.Add(entry);
    }

    private void OnSelectAllClicked(object? sender, RoutedEventArgs e)
    {
        if (ActiveListing() is { } list && _shell.ActiveTab is { } pane)
            SelectWholeFolder(list, pane);
    }

    /// <summary>
    /// The heading's box. Asks the pane what is chosen rather than the box what
    /// it has just become — see <see cref="SelectAllFrom"/> for why those are
    /// different questions.
    ///
    /// Through the ListBox for the same reason the Select ▸ All menu row is:
    /// filling the bound collection row by row fires a change per file, and
    /// each one refreshes the details panel and recomputes the summary.
    ///
    /// **A three-state box keeps whatever the click cycled it to unless it is
    /// told otherwise.** IsChecked is bound OneWay, so the click's own value
    /// sits on the control until the pane raises AllChosen again — and a click
    /// that changes nothing raises nothing. In an empty folder SelectAll had
    /// nothing to select, so the box was left showing a tick over a listing
    /// with no rows in it: the one thing AllChosen refuses to say anywhere
    /// else. Asking the pane again puts the answer back.
    /// </summary>
    private void OnSelectAllBoxClicked(object? sender, RoutedEventArgs e)
    {
        if (_shell.ActiveTab is not { } pane) return;

        if (!SelectAllFrom(pane.AllChosen)) SelectNone();
        else if (ActiveListing() is { } list) SelectWholeFolder(list, pane);

        pane.RefreshSelectionBoxes();
    }

    private void OnSelectNoneClicked(object? sender, RoutedEventArgs e) => SelectNone();

    private void OnInvertSelectionClicked(object? sender, RoutedEventArgs e)
        => InvertSelection();

    private bool SidebarShowing => _shell?.Sidebar.IsPanelVisible == true;

    /// <summary>
    /// Which region the keyboard is in.
    ///
    /// The sidebar is asked FIRST and the listing last, because the sidebar
    /// contains a list of its own — testing for a ListBox before ruling the
    /// sidebar out would call a place row part of the listing.
    ///
    /// **Anything else is Elsewhere, not the listing.** A toolbar button, the
    /// tab strip, a crumb, or nothing at all at startup are all real states,
    /// and calling them the listing would make F6 move ON from them — which is
    /// exactly the rescue the old handler existed to provide.
    /// </summary>
    private Input.KeyboardRegion CurrentRegion()
    {
        if (FocusManager?.GetFocusedElement() is not Visual focused)
            return Input.KeyboardRegion.Elsewhere;

        if (this.FindControl<Border>("SidebarPanel") is { } sidebar
            && IsInside(focused, sidebar))
            return Input.KeyboardRegion.Sidebar;

        // **Asked of the pane, not found by name.** The address bar lives inside
        // a per-pane template and has no generated field, so FindControl on the
        // window does not reach it — and a region test that silently answers
        // "somewhere else" would make the sidebar unreachable by keyboard while
        // every other step still worked.
        //
        // A focused text box while the bar is open IS that bar: it closes the
        // moment the keyboard leaves it, so no other box can hold focus at the
        // same time.
        if (focused is TextBox && _shell?.ActiveTab?.IsPathEditing == true)
            return Input.KeyboardRegion.Location;

        return focused.FindAncestorOfType<ListBox>(includeSelf: true) is not null
            ? Input.KeyboardRegion.Listing
            : Input.KeyboardRegion.Elsewhere;
    }

    private static bool IsInside(Visual child, Visual parent)
    {
        for (var visual = child; visual is not null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, parent)) return true;

        return false;
    }

    private void GoToRegion(Input.KeyboardRegion region)
    {
        switch (region)
        {
            case Input.KeyboardRegion.Location:
                _shell?.ActiveTab?.BeginEditPath();
                break;

            case Input.KeyboardRegion.Sidebar:
                // **Closed first, then queued BEHIND what closing sets off.**
                // The address bar's own lost-focus rule shuts it the moment
                // the sidebar takes the keyboard, and shutting it posts "put
                // the keyboard back in the listing" — a deliberate behaviour
                // for every other way the bar closes, and one that would land
                // after this and undo it. Same priority, posted second, so it
                // runs second: the listing is focused and then the sidebar row
                // takes it, which is the order asked for.
                _shell?.ActiveTab?.RevertPathText();

                Dispatcher.UIThread.Post(
                    () => FirstSidebarRow()?.Focus(NavigationMethod.Directional),
                    DispatcherPriority.Background);
                break;

            default:
                // Closed first, or the box's own lost-focus command fires as
                // the listing takes the keyboard and lands on whatever is
                // active by then.
                if (_shell?.ActiveTab is { IsPathEditing: true } editing)
                    editing.RevertPathText();

                ActiveListing()?.Focus();
                break;
        }
    }

    /// <summary>
    /// Where F6 puts the keyboard when it reaches the sidebar.
    ///
    /// **A section heading is a Button.** Folding made every heading a
    /// ToggleButton, which derives from Button, so the first visible one in
    /// the panel became PLACES rather than Home — and F6 landed on a control
    /// whose Space bar folds the list you were trying to reach. The rule is
    /// still "the first row", and a heading is not a row.
    ///
    /// Written with a body rather than as an expression so the rule can be
    /// pinned: RepoSource.Body ends a method at a closing brace at class
    /// indentation, and an expression-bodied member has none — it would have
    /// returned this declaration plus the whole of the next method.
    /// </summary>
    private Control? FirstSidebarRow()
    {
        if (this.FindControl<Border>("SidebarPanel") is not { } sidebar) return null;

        return sidebar.GetVisualDescendants().OfType<Button>()
                      .FirstOrDefault(b => b.IsVisible
                                           && b is not Avalonia.Controls.Primitives.ToggleButton);
    }

    /// <summary>
    /// Everything the arrow keys stop on in the sidebar, top to bottom.
    ///
    /// **RepeatButton and ToggleButton both derive from Button, and the panel's
    /// content sits in a ScrollViewer.** A scrollbar's PART_LineUpButton is a
    /// Button, so an unfiltered walk would let End put the keyboard on a scroll
    /// arrow — the same refusal ListForEmptySpace and TabStripEmptySpaceAt
    /// already make, for the same reason. Derived rather than trusted: whether
    /// the theme happens to mark those arrows unfocusable is a template detail,
    /// and this rule must not depend on one.
    ///
    /// A section HEADING is a stop, unlike F6's landing. It is the only way to
    /// unfold a section from the keyboard, and a folded section whose heading
    /// cannot be reached is a section the keyboard can never open again.
    /// </summary>
    private List<Control> SidebarStops()
    {
        if (this.FindControl<Border>("SidebarPanel") is not { } sidebar) return [];

        return [.. sidebar.GetVisualDescendants()
                          .OfType<Button>()
                          .Where(b => b is not RepeatButton)
                          .Where(b => b.Focusable && b.IsEffectivelyVisible && b.IsEffectivelyEnabled)];
    }

    /// <summary>
    /// Moves the keyboard through the sidebar.
    ///
    /// Focused as a DIRECTIONAL move rather than a plain one, so the row draws
    /// its focus ring: Avalonia sets :focus-visible for keyboard navigation and
    /// leaves it off for a click, and a keyboard walk with no visible cursor is
    /// a walk you cannot follow.
    /// </summary>
    private void MoveInSidebar(Input.SidebarStep step)
    {
        var stops = SidebarStops();

        var from = FocusManager?.GetFocusedElement() is Visual focused
            ? stops.FindIndex(s => ReferenceEquals(s, focused))
            : -1;

        var landing = Input.SidebarWalk.Landing(stops.Count, from, step);

        if (landing < 0) return;

        stops[landing].Focus(NavigationMethod.Directional);
    }

    private ListBox? ActiveListing()
    {
        foreach (var list in Lists(this))
            if (list.IsVisible
                && ReferenceEquals(list.DataContext, _shell.ActiveTab)
                && list.SelectionMode.HasFlag(SelectionMode.Multiple))
                return list;

        return null;
    }

    private static IEnumerable<ListBox> Lists(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is ListBox list) yield return list;

            foreach (var nested in Lists(child)) yield return nested;
        }
    }

    private static IEnumerable<ListBoxItem> Rows(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is ListBoxItem row) yield return row;

            foreach (var nested in Rows(child)) yield return nested;
        }
    }

    /// <summary>How close to an edge starts scrolling, and how fast.</summary>
    private const double EdgeZone = 28;

    private DispatcherTimer? _bandScroll;
    private double _bandScrollBy;

    /// <summary>
    /// Scrolls the listing while the pointer sits near its top or bottom edge.
    ///
    /// **A timer, not a nudge per pointer-move.** Move events only arrive while
    /// the pointer is moving, so scrolling on them alone means the band stops the
    /// instant you hold still at the edge — which is exactly when you want it to
    /// keep going.
    /// </summary>
    private void AutoScroll(ListBox list, Point pointer)
    {
        if (Scroller(list) is not { } scroller)
        {
            StopBandScroll();
            return;
        }

        // The list's own box, in the overlay's coordinates — the band and the
        // pointer are already measured there.
        if (list.TranslatePoint(default, BandLayer) is not { } origin)
        {
            StopBandScroll();
            return;
        }

        var top = origin.Y;
        var bottom = origin.Y + list.Bounds.Height;

        // Proportional to how far into the zone the pointer is, so easing toward
        // the edge eases the speed rather than switching it on.
        _bandScrollBy =
            pointer.Y < top + EdgeZone ? -(EdgeZone - (pointer.Y - top)) / EdgeZone * 24
            : pointer.Y > bottom - EdgeZone ? (EdgeZone - (bottom - pointer.Y)) / EdgeZone * 24
            : 0;

        if (Math.Abs(_bandScrollBy) < 0.5) { StopBandScroll(); return; }

        if (_bandScroll is not null) return;

        _bandScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _bandScroll.Tick += (_, _) =>
        {
            if (_bandList is not { } live || Scroller(live) is not { } view)
            {
                StopBandScroll();
                return;
            }

            var next = Math.Clamp(view.Offset.Y + _bandScrollBy, 0,
                                  Math.Max(0, view.Extent.Height - view.Viewport.Height));

            if (Math.Abs(next - view.Offset.Y) < 0.01) return;

            view.Offset = view.Offset.WithY(next);

            // The rows under the band have moved, so the selection has to be
            // recomputed against the rectangle as it now stands.
            ApplyBand(live, _bandRect);
        };

        _bandScroll.Start();
    }

    private void StopBandScroll()
    {
        _bandScroll?.Stop();
        _bandScroll = null;
    }

    /// <summary>The listing under the pointer, whatever part of a row it is
    /// over.</summary>
    private static ListBox? ListAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is ListBox list) return list;
        }

        return null;
    }

    /// <summary>
    /// Scrolls the listing while a DRAG rests near its top or bottom edge.
    ///
    /// **A folder that was off-screen could not be dropped into.** The listing
    /// held still for the whole drag, so reaching anything below the fold meant
    /// abandoning the drag, scrolling, and starting again — and in a folder of
    /// any size that is most of it. Both references scroll at the edges.
    ///
    /// It borrows the rubber band's timer, which is safe because a band and a
    /// file drag cannot both be in progress: the band needs a left press on
    /// empty space, and by the time a drag is running that press has been spent
    /// on the drag.
    /// </summary>
    private void DragScroll(DragEventArgs e)
    {
        if (ListAt(e.Source) is not { } list)
        {
            StopDragScroll();
            return;
        }

        _bandList = list;
        AutoScroll(list, e.GetPosition(BandLayer));
    }

    private void StopDragScroll()
    {
        StopBandScroll();
        _bandList = null;
    }

    private PaneViewModel? _hoverTab;
    private DispatcherTimer? _hoverSwitch;

    /// <summary>
    /// Switches to a tab the pointer has rested on while dragging.
    ///
    /// **A file could not be dragged into another tab at all.** The only way
    /// across was the split view — open the other side, drag, close it again —
    /// for a move that both references do by hovering. Without the switch the
    /// drop would also be blind: the destination would be a folder you cannot
    /// see, which is not a thing to ask anyone to aim at.
    ///
    /// Six hundred milliseconds: long enough that dragging ACROSS the strip to
    /// reach the listing does not shuffle through every tab on the way, short
    /// enough not to feel stuck.
    /// </summary>
    private void HoverTab(PaneViewModel? tab)
    {
        if (ReferenceEquals(tab, _hoverTab)) return;

        _hoverTab = tab;
        _hoverSwitch?.Stop();
        _hoverSwitch = null;

        if (tab is null) return;

        _hoverSwitch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _hoverSwitch.Tick += (_, _) =>
        {
            _hoverSwitch?.Stop();
            _hoverSwitch = null;

            // Read again rather than captured: the pointer may have moved on
            // between the tick being queued and it running.
            if (_hoverTab is not { } want) return;

            foreach (var group in new[] { _shell.Left, _shell.Right })
                if (group is not null && group.Tabs.Contains(want))
                    group.ActiveTab = want;
        };

        _hoverSwitch.Start();
    }

    private static ScrollViewer? Scroller(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is ScrollViewer found) return found;

            if (Scroller(child) is { } nested) return nested;
        }

        return null;
    }

    private void EndBand()
    {
        StopBandScroll();

        _bandList = null;
        _bandKept = null;

        // Released here as well as armed on the press: the list it names may be
        // torn down before the next band starts, and holding rows from a
        // listing that has gone would keep them alive for nothing.
        _bandTaken.Clear();

        SelectionBand.IsVisible = false;
    }

    private async Task BeginDragAsync(PaneViewModel pane, PointerPressedEventArgs trigger)
    {
        // The snapshot taken when the button went down wins: by now the press
        // has collapsed a multi-selection to the one row under the pointer.
        var paths = _dragSelection is { Count: > 0 } remembered
            ? remembered
            : pane.Selection.Count > 0
                ? pane.Selection.Select(x => x.FullPath).ToList()
                : pane.SelectedEntry is { } one ? [one.FullPath] : [];

        if (paths.Count == 0) return;

        // Asked before a payload is built, so the refusal reaches the status bar
        // instead of the drag reaching a target that cannot say why it failed.
        if (!pane.CanDragOut()) return;

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        _dragging = true;
        _internalDrag = true;
        _rightDragInFlight = _dragRight;

        // The release that ends this drag is a right-button release, and a
        // right-button release is how context menus open. Armed here, consumed
        // by the tunnelled ContextRequested handler.
        if (_dragRight) _suppressContextMenu = true;

        try
        {
            // DataFormat.File is what other applications actually read; Avalonia
            // serialises it to text/uri-list on X11, the same route the
            // clipboard takes.
            var data = new DataTransfer();

            foreach (var path in paths)
            {
                IStorageItem? item = Directory.Exists(path)
                    ? await storage.TryGetFolderFromPathAsync(path)
                    : await storage.TryGetFileFromPathAsync(path);

                if (item is not null) data.Add(DataTransferItem.CreateFile(item));
            }

            if (data.Items.Count == 0) return;

            // Not disposed — the drag system takes ownership.
            await DragDrop.DoDragDropAsync(
                trigger, data,
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] drag failed: {ex.Message}");
        }
        finally
        {
            _dragging = false;
            _internalDrag = false;
            _rightDragInFlight = false;
            _dragRight = false;
            _dragSource = null;
            _dragTrigger = null;
            _dragSelection = null;
        }
    }

    /// <summary>
    /// Takes files a drop offers that are not on disk — dragged straight out of
    /// 7-Zip, or Explorer's own zip view. Null on a platform with no such
    /// notion, which is every one but Windows.
    /// </summary>
    private Vaktari.Core.FileSystem.IVirtualFileDrop? _virtualDrop;

    /// <summary>The platform's shortcut writer, or null where the idea does
    /// not exist — the gestures that use it simply do not offer it then.</summary>
    private Vaktari.Core.FileSystem.IShortcutMaker? _shortcuts;

    /// <summary>
    /// True while the drag in flight was started with the RIGHT button, which
    /// changes what the drop does: nothing, until a menu asks.
    /// </summary>
    private bool _rightDragInFlight;

    /// <summary>Armed at the press that may become a right-drag.</summary>
    private bool _dragRight;

    /// <summary>
    /// Eats the context menu the right-button RELEASE would otherwise open.
    /// A right-drag ends in a release like any right-click, and without this
    /// the source row popped its menu the moment the drop finished — two
    /// gestures answering one another.
    /// </summary>
    private bool _suppressContextMenu;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        // The sidebar first: a place row has no pane above it, so asking for
        // one would refuse the drop before the place was ever considered.
        var spot = TargetAt(e.Source);

        // Before the refusal below: a tab strip is not a pane and not a place,
        // so a drag resting on it would be refused and never counted as a hover.
        HoverTab(TabAt(e.Source));

        if (!spot.Exists)
        {
            e.DragEffects = DragDropEffects.None;
            HighlightDropTarget(null);
            StopDragScroll();
            return;
        }

        // **The bin is a verb, not a folder**, so it is answered here rather
        // than falling through to the destination rules below — which would ask
        // what it costs to copy into "vaktari:trash" and refuse.
        //
        // Move, because that is what dropping on the bin does to the original,
        // and it is the effect Explorer's own cursor shows over the Recycle Bin.
        if (spot.IsBin)
        {
            e.DragEffects = Input.DroppedFileReader.Offered(e.DataTransfer).Count > 0
                ? DragDropEffects.Move
                : DragDropEffects.None;

            HighlightDropTarget(null, place: VirtualPaths.Trash);
            StopDragScroll();
            return;
        }

        // Near an edge of the listing, keep it moving — checked on every
        // drag-over rather than only where the drop would be accepted, because
        // scrolling is how you REACH somewhere that would accept it.
        DragScroll(e);

        var place = spot.Place;
        var pane = spot.Pane;
        var destination = spot.Destination;

        // A virtual listing is a view, not a folder, so its background has
        // nowhere to put anything. The paste path refuses it too, but that
        // refusal arrives as a line of status text after the drop; the cursor
        // can say it beforehand, which is when it is still useful. Read from
        // `destination` rather than the pane, so a real folder ROW inside
        // Recent still takes a drop.
        if (VirtualPaths.IsVirtual(destination))
        {
            e.DragEffects = DragDropEffects.None;
            HighlightDropTarget(null);
            return;
        }

        // Refuse a drop that would achieve nothing, so the cursor says so
        // before the click rather than a duplicate appearing after it.
        // **The effect first, from the raw paths, then what the drop means.**
        // Copying keeps a file dropped into its own folder — that is Explorer's
        // duplicate gesture — while moving discards it as a no-op, so the
        // filtering cannot be decided before the intent is.
        var offered = Input.DroppedFileReader.Offered(e.DataTransfer);
        var effect = EffectFor(e.KeyModifiers, offered, destination);

        var takeable = Input.DroppedFileReader
            .Read(e.DataTransfer, destination, effect == DragDropEffects.Copy).Any;

        // Files that live inside an archive have no paths yet, so nothing above
        // sees them — but they can be had, and the cursor has to say so before
        // the button is released rather than after.
        if (!takeable && _virtualDrop?.Offers(e.DataTransfer) == true)
        {
            // Copy: there is no original to take away. A move out of an archive
            // is not a thing the archive would survive.
            e.DragEffects = DragDropEffects.Copy;
            HighlightDropTarget(place is null ? pane : null, spot.Folder, spot.Place);
            return;
        }

        if (!takeable)
        {
            e.DragEffects = DragDropEffects.None;
            HighlightDropTarget(null);
            return;
        }

        e.DragEffects = effect;

        // A place is its own target; highlighting a pane for it would point at
        // the wrong half of the window. The row and the place are marked
        // whichever it is, so the ring is on the thing the files will go into.
        HighlightDropTarget(place is null ? pane : null, spot.Folder, spot.Place);
    }

    /// <summary>
    /// Copy or move. See <see cref="Input.DragEffect"/> for the rule — which is
    /// Windows's, by volume, rather than the one this used to apply.
    /// </summary>
    private Input.DragIntent IntentFor(
        KeyModifiers modifiers, IReadOnlyList<string> sources, string destination)
    {
        var intent = Input.DragEffect.For(
            modifiers.HasFlag(KeyModifiers.Control),
            modifiers.HasFlag(KeyModifiers.Shift),
            _internalDrag, sources, destination);

        // A platform with no idea of a shortcut must not advertise one on the
        // cursor and then quietly copy.
        return intent == Input.DragIntent.Link && _shortcuts is null
            ? Input.DragIntent.Copy
            : intent;
    }

    private DragDropEffects EffectFor(
        KeyModifiers modifiers, IReadOnlyList<string> sources, string destination)
        => IntentFor(modifiers, sources, destination) switch
        {
            Input.DragIntent.Move => DragDropEffects.Move,
            Input.DragIntent.Link => DragDropEffects.Link,
            _ => DragDropEffects.Copy,
        };

    /// <summary>
    /// Writes the drop's virtual files somewhere real and moves them into
    /// place. False when there were none, or none could be had.
    ///
    /// **Moved, not copied.** What comes back was written to a temporary folder
    /// for this drop alone and has no original to preserve, so copying would
    /// leave a duplicate behind for nobody.
    ///
    /// **On the thread that received the drop, and that is not a detail.** This
    /// used to run on the thread pool, to keep a large archive from freezing the
    /// window mid-gesture, and it cost the feature: what a drag hands over is a
    /// COM object belonging to the apartment that received it, and reading it
    /// from anywhere else fails every file in the drop. Worse, the pool work
    /// began only after the drop handler had returned, by which time the source
    /// is entitled to have taken the object away — so the same drag that
    /// Offers had just accepted refused everything a moment later.
    ///
    /// That is what "nothing came out of that archive" was: not an empty
    /// archive, but every entry refused, silently, in the wrong apartment.
    /// The window is held for the length of the unpack instead, which is what
    /// the bounds in VirtualFileDrop are for.
    /// </summary>
    private bool TakeVirtual(IDataTransfer data, PaneViewModel pane, string destination)
    {
        if (_virtualDrop is not { } virtualDrop || !virtualDrop.Offers(data)) return false;

        pane.Status = "taking the files out of the archive…";

        IReadOnlyList<string> taken;

        try
        {
            taken = virtualDrop.Take(data);
        }
        catch (Exception ex)
        {
            pane.Status = $"could not take those out of the archive: {ex.Message}";
            return true;
        }

        if (taken.Count == 0)
        {
            pane.Status = "nothing came out of that archive";
            return true;
        }

        pane.PasteIntoFolder(destination, taken, move: true);

        return true;
    }

    /// <summary>
    /// Where rescued drops are staged. Under our own name inside the temporary
    /// directory, so a crash leaves something recognisable rather than litter,
    /// and so the operating system clears it eventually even if we do not.
    /// </summary>
    private static string DropStagingRoot()
        => Path.Combine(Path.GetTempPath(), "Vaktari", "drops");

    /// <summary>
    /// What the drag actually handed over, and whether it was still there when
    /// it did.
    ///
    /// **Written because a failure said "Could not find file" about a path the
    /// drop had just been given.** Dragging out of 7-Zip does not hand over the
    /// archive's contents: it extracts them to a temporary folder of its own
    /// and hands over paths into that. The copy those paths feed runs after the
    /// drop returns, so there are two quite different explanations for a file
    /// that is not there — 7-Zip cleaned up before we read it, or 7-Zip had not
    /// finished writing it when the drop completed — and the fix differs. This
    /// says which, at the one instant that separates them.
    ///
    /// Bounded: a drag of a large tree should not be turned into a long walk by
    /// the thing reporting on it.
    /// </summary>
    private static void ReportDroppedPaths(IReadOnlyList<string> paths)
    {
        Console.Error.WriteLine($"[vaktari] drop: {paths.Count} path(s) handed over");

        for (var i = 0; i < paths.Count && i < 8; i++)
        {
            var path = paths[i];

            if (File.Exists(path))
            {
                Console.Error.WriteLine($"[vaktari] drop:   file present · {path}");
                continue;
            }

            if (!Directory.Exists(path))
            {
                Console.Error.WriteLine($"[vaktari] drop:   ALREADY GONE · {path}");
                continue;
            }

            // A folder is the case that matters: the failure named a file
            // inside one, so the question is whether the contents had been
            // written yet, not whether the folder existed.
            var files = 0;
            var bytes = 0L;

            try
            {
                // Links not followed: a dropped folder holding a link to a huge
                // tree would otherwise be measured as that tree, and one
                // pointing at an ancestor would spin until the two-thousand cap
                // saved it by accident.
                foreach (var inside in Vaktari.Core.FileSystem.SafeWalk.Descend(path))
                {
                    if (inside.IsDirectory || inside.IsLink) continue;

                    files++;
                    try { bytes += new FileInfo(inside.Path).Length; } catch { }
                    if (files >= 2_000) break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[vaktari] drop:   folder unreadable · {path}");
                continue;
            }

            Console.Error.WriteLine(
                $"[vaktari] drop:   folder with {files} file(s), {bytes} bytes · {path}");
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        HighlightDropTarget(null);
        StopDragScroll();
        HoverTab(null);
    }

    /// <summary>
    /// Marks what a drop would land in: the pane, the folder row inside it, and
    /// the sidebar place.
    ///
    /// **Only the pane was ever marked, and the pane is the one thing that was
    /// never in doubt.** What a drag could not tell you is whether releasing
    /// puts the files into the folder under the pointer or into the folder
    /// being listed — different places, and finding out meant releasing and
    /// looking. All three are cleared together, because a stale ring on a row
    /// you have moved off is worse than none.
    /// </summary>
    private void HighlightDropTarget(PaneViewModel? pane, string? row = null, string? place = null)
    {
        foreach (var group in new[] { _shell.Left, _shell.Right })
        {
            if (group is null) continue;

            foreach (var tab in group.Tabs)
            {
                var here = ReferenceEquals(tab, pane);

                tab.IsDropTarget = here;
                tab.DropTargetPath = here ? row ?? "" : "";
            }
        }

        foreach (var group in _shell.Sidebar.Groups)
            foreach (var row2 in group.Places)
                row2.IsDropTarget = place is not null && PathRules.Same(row2.Path, place);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        HighlightDropTarget(null);
        StopDragScroll();
        HoverTab(null);

        // **The bin takes drops.** Its row is AllowDrop with a comment about
        // taking them "the way the tree and Quick access do in Explorer", and
        // nothing ever mapped one to IFileOperations.Trash — PlaceAt refuses a
        // virtual path, and the bin's path is the virtual vaktari:trash, so the
        // drop landed nowhere and looked like the row was simply dead.
        var spot = TargetAt(e.Source);

        if (spot.IsBin && _shell.ActiveTab is { } binPane)
        {
            var offered = Input.DroppedFileReader.Offered(e.DataTransfer);

            if (offered.Count > 0) binPane.TrashPaths(offered);
            return;
        }

        // A sidebar place and a breadcrumb are destinations in their own right,
        // and neither is guaranteed to have a pane above it to ask about — so
        // the active tab stands in as the pane that reports what happened.
        var pane = spot.Pane ?? (spot.Exists ? _shell.ActiveTab : null);

        if (pane is null) return;

        // Dropping onto a folder row means into that folder, not into the
        // directory being listed — that is what the pointer was over.
        var target = spot.Explicit;
        var destination = target ?? pane.CurrentPath;

        var intent = IntentFor(
            e.KeyModifiers, Input.DroppedFileReader.Offered(e.DataTransfer), destination);

        var move = intent == Input.DragIntent.Move;

        var dropped = Input.DroppedFileReader.Read(e.DataTransfer, destination, !move);

        if (!dropped.Any)
        {
            // **The contents of an archive, which have no paths until asked
            // for.** This is what dragging out of 7-Zip carries, and looking
            // only for paths saw an empty drop — so the drag did nothing at all
            // and read as the application being unreliable.
            if (TakeVirtual(e.DataTransfer, pane, destination))
            {
                e.Handled = true;
                return;
            }

            // Said rather than silently ignored: a drop that does nothing is
            // indistinguishable from one that missed the pane.
            if (dropped.Refusal.Length > 0) pane.Status = dropped.Refusal;

            e.Handled = true;
            return;
        }

        var paths = dropped.Paths;

        ReportDroppedPaths(paths);

        // **Before this handler returns, because after it returns the files may
        // not exist.** Dragging out of an archive hands over a path into the
        // archiver's own temporary folder, and the archiver deletes it the
        // moment the drop is over — measured at 541 files and 8,985,809 bytes
        // present at the drop, and the whole folder gone by the time the copy
        // ran. See DropStaging.
        var rescue = Input.DropStaging.Rescue(
            paths, Path.GetTempPath(), Path.Combine(DropStagingRoot(), Guid.NewGuid().ToString("N")[..12]));

        if (rescue.Rescued)
        {
            // The two figures separate a healthy rescue from a degraded one: a
            // move is a rename and costs nothing, while copied bytes are time
            // this thread spent frozen — if that number is ever large, the
            // fallback is being hit and the log says so.
            Console.Error.WriteLine(
                $"[vaktari] drop: rescued from the temporary folder before the source "
                + $"could clear it — {rescue.Moved} moved"
                + (rescue.CopiedBytes > 0 ? $", {rescue.CopiedBytes} bytes copied" : ""));

            paths = rescue.Paths;

            // Moved out of our own staging folder rather than copied, so
            // nothing is left behind. What the user asked of the ORIGINAL is
            // already satisfied: the archive still holds it either way.
            move = true;
        }

        // Shortcuts, before anything else can spend work: nothing is copied or
        // moved for a link, and the rescue above never fires for one because
        // internal drags are not volatile.
        if (intent == Input.DragIntent.Link && _shortcuts is { } shortcuts)
        {
            CreateShortcuts(shortcuts, pane, paths, destination);
            e.Handled = true;
            return;
        }

        // **A right-drag executes nothing.** The whole point of dragging with
        // the right button is that the drop ASKS — Explorer's oldest answer to
        // "did I just move that or copy it". The menu holds everything needed
        // to carry on; letting the drop fall through would decide for the user
        // the one time they explicitly asked to be consulted.
        if (_internalDrag && _rightDragInFlight)
        {
            ShowRightDropMenu(pane, target, destination, paths, defaultsToMove: move);
            e.Handled = true;
            return;
        }

        Paste(pane, target, paths, move);

        e.Handled = true;
    }

    /// <summary>The one place a drop's files actually go somewhere, so the
    /// direct path and the right-drag menu cannot drift apart.</summary>
    private static void Paste(
        ViewModels.PaneViewModel pane, string? target, IReadOnlyList<string> paths, bool move)
    {
        if (target is not null)
            pane.PasteIntoFolder(target, paths.ToList(), move);
        else
            pane.PasteInto(paths.ToList(), move);
    }

    /// <summary>
    /// Makes a shortcut per dropped item, and says what happened — including
    /// the refusal for files that live in somebody's temporary folder, where a
    /// shortcut would dangle the moment its owner tidies up.
    /// </summary>
    private void CreateShortcuts(
        Vaktari.Core.FileSystem.IShortcutMaker shortcuts,
        ViewModels.PaneViewModel pane,
        IReadOnlyList<string> paths,
        string destination)
    {
        var made = 0;
        var doomed = 0;

        foreach (var path in paths)
        {
            if (Input.DropStaging.IsVolatile(path, Path.GetTempPath()))
            {
                doomed++;
                continue;
            }

            try
            {
                shortcuts.CreateShortcut(path, destination);
                made++;
            }
            catch (Exception ex)
            {
                pane.Status = Vaktari.Core.FileSystem.Failures.Describe(ex, "make that shortcut");
                return;
            }
        }

        pane.Status = doomed > 0
            ? $"created {made} shortcut(s) — {doomed} skipped: files inside an archive have no lasting place to point at"
            : $"created {made} shortcut(s)";
    }

    /// <summary>
    /// The right-drag's question, asked where the button was released. The
    /// default the plain drop would have taken leads and is emphasised, the
    /// way Explorer bolds its default; closing the menu without choosing is
    /// the cancel.
    /// </summary>
    private void ShowRightDropMenu(
        ViewModels.PaneViewModel pane,
        string? target,
        string destination,
        IReadOnlyList<string> paths,
        bool defaultsToMove)
    {
        var kept = paths.ToList();

        MenuItem Option(string header, bool emphasised, Action run)
        {
            var item = new MenuItem { Header = header };

            if (emphasised) item.FontWeight = Avalonia.Media.FontWeight.Bold;

            item.Click += (_, _) => run();
            return item;
        }

        var menu = new MenuFlyout();

        var moveItem = Option("Move here", defaultsToMove, () => Paste(pane, target, kept, move: true));
        var copyItem = Option("Copy here", !defaultsToMove, () => Paste(pane, target, kept, move: false));

        // Move first when it is the default, copy first otherwise — the
        // emphasised answer is also the nearest one.
        if (defaultsToMove) { menu.Items.Add(moveItem); menu.Items.Add(copyItem); }
        else { menu.Items.Add(copyItem); menu.Items.Add(moveItem); }

        if (_shortcuts is { } shortcuts)
            menu.Items.Add(Option("Create shortcuts here", false,
                () => CreateShortcuts(shortcuts, pane, kept, destination)));

        menu.Items.Add(new Separator());
        menu.Items.Add(Option("Cancel", false, static () => { }));

        // At the pointer, which at this instant is exactly the drop point.
        menu.ShowAt(this, showAtPointer: true);
    }

    // ---- geometry ------------------------------------------------------

    /// <summary>
    /// The saved size and position, put back.
    ///
    /// **This rejected anything at or below 200 and let everything else
    /// through, and 200 is not a number this window knows anything about.** The
    /// floor is MinWidth/MinHeight in the markup, and the note beside them says
    /// a session that saved something smaller is clamped up on restore — which
    /// was true only because Avalonia raises Width to MinWidth when the window
    /// is measured, not because anything here did it. Between this call and that
    /// first measure the property holds the undersized number, and
    /// CaptureGeometry reads the property.
    ///
    /// So the two guards now say the two different things they were conflating.
    /// Zero is what an absent key deserializes to — the note beside the
    /// per-layout scales in SessionModel records that these initializers do not
    /// run — and an absent size must leave the window the size the markup opens
    /// it at, NOT shrink it to the smallest one allowed. Anything else is a real
    /// saved size, and a real saved size below the floor is raised to the floor,
    /// because it is unusable whether it was chosen this session or last.
    ///
    /// Internal rather than private so WindowFloorTests can hand it a session
    /// directly. The store this is otherwise fed from is the real one on the
    /// machine running the suite.
    ///
    /// <paramref name="index"/> is which saved window this one is. It defaults
    /// to the first so the tests that hand a session straight in are unchanged,
    /// and ElementAtOrDefault answers null for the negative index a window
    /// opened from another one carries — such a window has no saved geometry
    /// and is placed beside its opener instead.
    /// </summary>
    internal void ApplyGeometry(SessionState? state, int index = 0)
    {
        if (state?.Windows.ElementAtOrDefault(index) is not { } w) return;

        if (w.Width > 0) Width = Math.Max(w.Width, MinWidth);
        if (w.Height > 0) Height = Math.Max(w.Height, MinHeight);

        if (w.X != 0 || w.Y != 0)
            Position = new PixelPoint((int)w.X, (int)w.Y);

        if (w.IsMaximized)
            WindowState = Avalonia.Controls.WindowState.Maximized;
    }

    /// <summary>
    /// A peer, on the folder it was asked for, carrying the view it was opened
    /// from.
    ///
    /// **The same principle NewTab states five lines above the command that
    /// leads here**: "A new tab that resets all five is a new tab you have to
    /// set up." A window is the heavier version of that tab, so it carries the
    /// same things — the sidebar width and rail, the folded sections, the split
    /// ratio, the zoom, and the tab's own layout, sort and hidden files through
    /// <c>like</c>. Font scale in particular is an accessibility setting rather
    /// than a preference: a window that arrives at 1.0 for somebody who works
    /// at 1.4 is a window they have to fix before they can read it.
    ///
    /// Geometry is deliberately NOT carried. The new window is offset beside
    /// the opener rather than stacked exactly on top of it, which is what makes
    /// it visible that a second one opened at all.
    /// </summary>
    private void OpenNewWindow(string? folder)
    {
        var seed = _shell.ToWindowSession() with
        {
            // The view, not the contents and not the frame. An empty pane list
            // is what leaves the tab to the folder that was asked for.
            Panes = [],
            RememberedRightPane = null,
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0,
            IsMaximized = false,
        };

        var window = new MainWindow(
            _services,
            restoreIndex: -1,
            openAt: string.IsNullOrWhiteSpace(folder) ? null : folder,
            seed: seed,
            like: _shell.ActiveTab);

        window.Show();

        // Offset only from a normal window: a maximized or minimized opener has
        // no useful position to step away from, and Position on a maximized
        // window is the screen corner.
        if (WindowState == WindowState.Normal)
            window.Position = new PixelPoint(Position.X + 32, Position.Y + 32);

        window.Raise();
    }

    /// <summary>
    /// Window <paramref name="index"/> of the saved session, opened by the
    /// founder after its own constructor has finished. Only the founder does
    /// this, so a restored window cannot recurse into opening more.
    /// </summary>
    private void RestoreWindow(int index)
    {
        var window = new MainWindow(
            _services, restoreIndex: index, openAt: null, seed: null, like: null);

        window.Show();
    }

    private WindowSession CaptureGeometry()
    {
        var maximized = WindowState == Avalonia.Controls.WindowState.Maximized;

        return new WindowSession
        {
            // While maximized the live bounds are the screen, not the size to
            // return to, so the stored values are left alone.
            X = maximized ? 0 : Position.X,
            Y = maximized ? 0 : Position.Y,
            Width = maximized ? 1000 : Width,
            Height = maximized ? 680 : Height,
            IsMaximized = maximized,
        };
    }

    /// <summary>
    /// A path, whether the desktop handed over a path or a file:// URI.
    ///
    /// **The installed desktop entry said %U and this did not decode one**, so
    /// on GNOME, Xfce, Cinnamon and plain xdg-open — every desktop that honours
    /// %U literally — "open containing folder" arrived as
    /// "file:///home/me/Documents", Directory.Exists said no, and the path was
    /// dropped without a word. That was the primary Linux install route, and it
    /// could not open a folder at all.
    ///
    /// packaging/install.sh now writes %F, matching brand/vaktari.desktop. This
    /// stays because a portal or a D-Bus caller can still send a URI whatever
    /// the Exec key says, and percent-decoding is the difference between
    /// opening "My Documents" and opening nothing.
    /// </summary>
    /// Moved to Vaktari.Core.FileSystem.FileUri, because there are two callers
    /// now — the command line and the desktop's own request channel — and two
    /// copies of a decoder is how one of them keeps a bug the other has already
    /// fixed. The shared one also refuses what it cannot open rather than
    /// handing the raw string on, which is the same silent drop by a different
    /// route.
    /// </summary>
    private static string? LocalPath(string raw) => FileUri.ToLocalPath(raw);

    /// <summary>
    /// Says which of the four things happened, on the same terminal as the
    /// running-from line. **A file manager that silently does not answer looks
    /// exactly like one that answered and did nothing**, and that is the whole
    /// failure being fixed here — so not answering has to say so.
    ///
    /// async void with a catch, like the other started-and-not-awaited handlers
    /// here: a discard would take the exception with the task.
    /// </summary>
    private static async void AnnounceFileManagerService(IFileManagerService service)
    {
        try
        {
            var state = await service.ReconcileAsync().ConfigureAwait(true);

            Console.Error.WriteLine(
                $"[vaktari] FileManager1: {FileManagerServiceStates.Describe(state)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] FileManager1: {ex.Message}");
        }
    }

    /// <summary>
    /// The three verbs, each routed to something the window already does. There
    /// is no new behaviour here at all — the value of this feature is that these
    /// three now have a name on the bus that other applications already call.
    ///
    /// **Items goes to ShowAsync and not OpenPaths**, which is the entire
    /// difference: OpenPaths opens the folder and selects nothing, and in a
    /// Downloads folder of four hundred files that does not answer "which one
    /// did I just save".
    /// </summary>
    internal async void OnShowRequested(ShowRequest request)
    {
        try
        {
            switch (request.Kind)
            {
                case ShowKind.Items:
                    await _shell.ShowAsync(request.Paths).ConfigureAwait(true);
                    break;

                case ShowKind.Folders:
                    // Raises the window itself, so it returns rather than
                    // falling through to a second Raise.
                    OpenPaths(request.Paths, activate: true);
                    return;

                case ShowKind.ItemProperties:
                    // Already refuses a path that has gone and says so in the
                    // status line, rather than filling a sheet with zeroes.
                    ShowPropertiesFor(request.Paths);
                    break;
            }

            Raise();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] FileManager1 request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Brings the window forward. Split out of OpenPaths so the bus's three
    /// verbs raise it the same way a handed-over launch does — somebody asked to
    /// SEE something, and loading it behind whatever they were doing is not that.
    /// </summary>
    private void Raise()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
    }

    /// <summary>
    /// Opens folders in tabs. Files resolve to the folder holding them, because
    /// "open containing folder" is the request the desktop actually sends.
    /// </summary>
    internal void OpenPaths(IReadOnlyList<string> paths, bool activate)
    {
        foreach (var raw in paths)
        {
            // **A URI this process cannot open now says so.** It used to be
            // handed on as its own raw text, fail Directory.Exists and vanish;
            // trash:/// and sftp:// are real things a desktop sends.
            if (LocalPath(raw) is not { } path)
            {
                _shell.OperationStatus = $"cannot open {raw}";
                continue;
            }

            if (File.Exists(path) && Path.GetDirectoryName(path) is { Length: > 0 } parent)
                path = parent;

            if (!Directory.Exists(path)) continue;

            _shell.OpenInNewTab(path);
        }

        if (!activate) return;

        // The user asked to see a folder, and silently loading it behind
        // whatever they were doing is not that.
        Raise();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved) return;

        // **The transfer question comes first, and it is not behind a
        // preference.** Closing a window used to kill its transfer and the
        // process was ending, so nothing survived to notice. With a second
        // window the process carries on: the handle would go on writing with no
        // bar showing it, no Cancel reaching it and nobody told when it failed.
        // The tabs question below is gated on a preference that is off by
        // default, and a question about losing work must not be.
        if (_shell.RunningDescription() is { } running)
        {
            e.Cancel = true;

            if (!await ConfirmCloseAsync(running)) return;

            // Said yes. The transfer belongs to this window's bar, and leaving
            // it running is the silent loss the question exists to prevent.
            _shell.CancelAllOperations();
        }

        // **One question, not two.** `else if` rather than a second `if`: a
        // window with a transfer running and six tabs open must not ask twice
        // in a row, and the transfer question is the one that costs something.
        // RunningDescription carries the tab count when both apply, so nothing
        // is hidden by the branch that did not run.
        //
        // Asked before anything is torn down, and only when there is something
        // to lose. Off by default: the session is restored on next launch, so
        // closing a window full of tabs is not actually destructive here — which
        // is exactly why this is a preference rather than the behaviour.
        else if (AppSettings.Current.General.ConfirmClosingMultipleTabs && CountOpenTabs() > 1)
        {
            e.Cancel = true;

            var confirmed = await ConfirmCloseAsync(
                $"{CountOpenTabs()} tabs are open. Close anyway?");

            if (!confirmed) return;
        }

        // Cancel, flush, then close for real. Awaiting inside an async void
        // handler does not hold the window open — the process can otherwise
        // exit with the write still in flight.
        e.Cancel = true;

        // Asked BEFORE the release below, which takes this window out of the
        // list — after it, a second-to-last window would look like the last.
        var last = _services.IsLastWindow;

        // The session goes first. It is the one whose loss the user would
        // actually notice, and it cannot fail because of a subprocess. What is
        // written, what is flushed and what is only released on the way out of
        // the PROCESS all live on the services, because they are the
        // application's and not this window's.
        await _services.ReleaseAsync(this);

        // **Only the last window out.** IFileSharing.StopAllAsync is documented
        // "called on shutdown so nothing outlives the app", and platform.Sharing
        // is ONE CopypartyShare for every window — its running dictionary holds
        // every server in the process and StopAllAsync kills the lot. Measured:
        // two shells built over one provider, a share started through the
        // first, and the second shell's StopAllSharesAsync emptied Active. So
        // closing one of two windows was killing the other's server while its
        // Sharing section went on listing the folder as served.
        //
        // A window is not the process. Stopping only on the way out of the
        // process is what the interface's own contract asks for; per-window
        // ownership of individual shares is a second stage, and until it exists
        // a share started anywhere outlives every window but the last.
        if (last)
        {
            try
            {
                await _shell.StopAllSharesAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vaktari] stopping shares failed: {ex.Message}");
            }
        }

        _closeApproved = true;
        Close();
    }

    /// <summary>
    /// What a closed window lets go of.
    ///
    /// **`_shell.Dispose()` on its own is not teardown, and that is measured**:
    /// after Dispose, CutMarks.Mark still set the shell's CutPaths, because
    /// Dispose only stopped the rate timer and tore down the panes. Nothing in
    /// this application unsubscribed from anything, which was harmless for as
    /// long as a window lived exactly as long as the process — and stops being
    /// harmless the moment one can close while the others carry on.
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        if (_theme is not null && _onThemeChanged is not null)
            _theme.Changed -= _onThemeChanged;

        _shell.Dispose();
    }

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

    private int CountOpenTabs()
    {
        var total = _shell.Left.Tabs.Count;
        if (_shell.Right is { } right) total += right.Tabs.Count;

        return total;
    }

    /// <summary>
    /// A real dialog rather than the prompt bar: the prompt bar lives inside
    /// the window being closed, and driving a close decision from a control
    /// that is about to be destroyed is the shape of bug this project has
    /// already paid for once with Shift+Delete.
    /// </summary>
    private async Task<bool> ConfirmCloseAsync(string question)
    {
        var dialog = new Window
        {
            Title = "Close Vaktari",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        AppIcon.Apply(dialog);

        var result = false;

        // **These two said "close anyway" and "cancel" while the four
        // dialogs beside them said "Cancel".** A window built in code is
        // still a window; sentence case is the one rule, and
        // LabelCasingTests reads this file for exactly that reason.
        var close = new Button { Content = "Close anyway", Padding = new Thickness(14, 4) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4) };

        close.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = question,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, close },
                },
            },
        };

        // Focused so Enter and Space reach a real button rather than a
        // hand-rolled key path.
        cancel.Focus();

        await dialog.ShowDialog(this);

        // Deliberately does NOT close. Calling Close() here would re-enter
        // OnClosing with _closeApproved still false and confirm forever; the
        // caller falls through to the existing flush-then-close path instead.
        return result;
    }

    // ---- per-pane wiring -----------------------------------------------

    private void WirePane(PaneViewModel pane)
    {
        pane.RenameRequested -= OnRenameRequested;
        pane.RenameRequested += OnRenameRequested;

        pane.ChooseApplicationRequested -= OnChooseApplicationRequested;
        pane.ChooseApplicationRequested += OnChooseApplicationRequested;

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

    /// <summary>Focus now happens through FocusBehavior.FocusOnVisible in the
    /// markup, since there is no field to focus from here.</summary>
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

    // ---- inline prompt -------------------------------------------------

    private enum PromptMode { None, Rename, RenamePlace, ConfirmDelete, ConfirmTrash, ConfirmEmptyTrash, Connect }

    private PromptMode _prompt = PromptMode.None;

    /// <summary>
    /// Whether a yes/no prompt is open and should own the keyboard.
    ///
    /// **One predicate, because the list was written out by hand and lost a
    /// member.** The keyboard check named ConfirmDelete and ConfirmEmptyTrash
    /// and not ConfirmTrash, so with "confirm move to trash" turned on, Enter at
    /// the trash prompt fell straight through to the ordinary key handling
    /// below — where Enter means OPEN. Answering "yes, bin these" launched them
    /// instead, and Escape cleared the filter rather than cancelling.
    ///
    /// It went unseen because the setting is off by default, so the prompt it
    /// breaks is one most people never see. The three text-entry modes are
    /// deliberately excluded: those are guarded by the focused-TextBox rule
    /// further down, which is a different question — whether something is being
    /// typed into, not whether a decision is pending.
    /// </summary>
    private bool IsConfirming => _prompt
        is PromptMode.ConfirmDelete
        or PromptMode.ConfirmTrash
        or PromptMode.ConfirmEmptyTrash;
    private FileEntry _renameTarget;

    /// <summary>
    /// The pane holding the row being renamed, or null.
    ///
    /// Held rather than re-read from <c>ActiveTab</c>, because the editor is
    /// drawn by that pane's own listing: clicking the other side while a name
    /// is being typed changes which tab is active and changes nothing about
    /// where the box is.
    /// </summary>
    private PaneViewModel? _renamePane;

    /// <summary>The rename the last confirm started, or null. Read only by
    /// <see cref="StepRenameAsync"/>, which must not step past a failure.</summary>
    private Task<bool>? _lastRename;

    /// <summary>The pinned row being renamed, for the same reason
    /// _renameTarget exists: the prompt bar is one bar for every prompt.</summary>
    private PlaceItemViewModel? _renamePlace;

    /// <summary>
    /// Naming a pinned place.
    ///
    /// Its own prompt mode rather than reusing Rename: that one is about a file
    /// and applies FileNames — a slash and a colon are refused there and are
    /// perfectly good text for a caption, and nothing is written to disk under
    /// this name.
    /// </summary>
    private void OnRenamePlaceRequested(object? sender, PlaceItemViewModel place)
    {
        if (PromptBar is null || PromptInput is null) return;

        _prompt = PromptMode.RenamePlace;
        _renamePlace = place;

        PromptLabel.Text = "Call it";
        PromptInput.Text = place.Label;
        PromptInput.IsVisible = true;
        PromptConfirm.Content = "Rename";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "enter to confirm · esc to cancel";
        PromptBar.IsVisible = true;

        PromptInput.Focus();
        PromptInput.SelectAll();
    }

    /// <summary>
    /// Opens the editor ON THE ROW.
    ///
    /// **A name was typed at the bottom of the window instead.** This filled
    /// the shared <c>PromptBar</c> — a bottom-docked strip whose TextBox is a
    /// fixed 320 wide — so renaming a row in a full-height listing happened as
    /// far from that row as the window is tall, with nothing at either end
    /// naming the file the other meant, and a listing that stays live behind an
    /// inline bar can move the selection out from under it. The bar is still
    /// there for the confirmations, for a pinned place's name and for a server
    /// address; it is the file rename that has left it.
    /// </summary>
    private void OnRenameRequested(object? sender, FileEntry entry)
    {
        if (_shell.ActiveTab is not { } pane) return;

        // **Any second request re-pointed an editor that was already open.** F2
        // was the loud way in; Ctrl+Shift+N is the quiet one — new folder, new
        // file and new-from-template all hand off to the rename when they are
        // done, and each raised this straight over a name somebody was still
        // typing. One editor, one tenant: whoever got here first keeps it, and
        // a caller that wants it must close the one it has.
        if (_prompt is not PromptMode.None) return;

        _prompt = PromptMode.Rename;
        _renameTarget = entry;
        _renamePane = pane;

        pane.RenameText = entry.Name;

        // LAST, and after the text: this is the line that puts a box on the row
        // and hands it the keyboard, and RenameBox selects the name out of
        // whatever the box already holds.
        pane.RenamingPath = entry.FullPath;
    }

    /// <summary>
    /// The single place a confirmed prompt is acted on, so the button and the
    /// keyboard cannot drift apart.
    /// </summary>
    private void ConfirmPrompt()
    {
        var mode = _prompt;
        var target = _shell.ActiveTab;

        // Read before closing: the action must not depend on UI state that the
        // closing itself tears down.
        //
        // A rename is typed on the row and every other prompt in the bar, so
        // the name comes from whichever one is open.
        var name = mode is PromptMode.Rename
            ? _renamePane?.RenameText ?? ""
            : PromptInput?.Text ?? "";

        var entry = _renameTarget;

        // **A refused name used to close the bar and report afterwards.** By
        // the time "that name is not one Windows will take" reached the status
        // line, the box holding the typed name was gone — so correcting one
        // character meant F2 and retyping the lot. A name of nothing but spaces
        // matched no case at all below and vanished without a word.
        //
        // Asked while the box is still open, so a refusal can stay in it.
        if (mode == PromptMode.Rename)
        {
            var decision = Input.RenamePrompt.Decide(name, entry.Name);

            if (decision.Verdict == Input.RenameVerdict.Refused)
            {
                // Held open under the box on the row, rather than written into
                // a hint line at the far end of the window. Nothing re-focuses:
                // the box already has the keyboard, which is where the Enter
                // that got here came from.
                if (_renamePane is not null) _renamePane.RenameRefusal = decision.Reason;

                return;
            }

            ClosePrompt();

            // **The file you had just renamed came back unselected**, in a
            // folder that had lost the rest of the selection with it. Named
            // here because this is the only place that knows the name: the
            // reload builds its rows from the file system and has never heard
            // of the one that was typed.
            //
            // HERE rather than in RenameOrThrowAsync, which is where it looks
            // like it belongs. A Tab that is stepping through a run renames and
            // then puts the keyboard on the NEXT row — and a request registered
            // from inside the rename lands after that, so the bar edited b.txt
            // while the listing highlighted the file just finished. Registered
            // from the prompt, the reload settles before the step chooses, and
            // the step has the last word.
            //
            // From the entry's own folder rather than CurrentPath: a search
            // listing holds rows from all over the machine.
            if (decision.Verdict == Input.RenameVerdict.Rename
                && target is not null
                && PathRules.Parent(entry.FullPath) is { } folder)
                target.SelectAfterLoad(Path.Combine(folder, decision.Name));

            // Kept, so a Tab that is stepping through a run can wait for it
            // and stop when the file system says no. Nothing else reads it.
            _lastRename = decision.Verdict == Input.RenameVerdict.Rename
                ? target?.TryRenameAsync(entry, decision.Name)
                : Task.FromResult(true);

            return;
        }

        ClosePrompt();

        switch (mode)
        {
            case PromptMode.RenamePlace:
                _ = _shell.RenamePlaceAsync(_renamePlace, name);
                break;

            // **The bin refused what this had just confirmed.** Its rows carry
            // the path the file used to occupy, which the file operations
            // cannot act on — so the prompt was shown, answered, and then
            // declined with "already in the bin". Asked and answered and
            // nothing happened is worse than never having offered.
            case PromptMode.ConfirmDelete when target is { IsTrashListing: true }:
                _ = target.PurgeFromTrashAsync();
                break;

            case PromptMode.ConfirmDelete:
                target?.DeleteSelectedCommand.Execute(null);
                break;

            case PromptMode.ConfirmTrash:
                target?.TrashSelectedCommand.Execute(null);
                break;

            case PromptMode.ConfirmEmptyTrash:
                _ = target?.EmptyTrashAsync();
                break;

            // Rename is answered above, before the bar closes, so that a
            // refusal can keep the typed name on screen. Tidying still happens
            // there: Windows drops a trailing space or dot at the API level, so
            // a name typed with one asks for something and gets something else.

            case PromptMode.Connect when !string.IsNullOrWhiteSpace(name):
                _ = _shell.ConnectToAsync(name.Trim());
                break;
        }
    }

    /// <summary>
    /// Reuses the prompt bar rather than adding a dialog: it already handles
    /// focus, Enter and Escape, and a server address is just another line of
    /// text to type.
    /// </summary>
    private void OnConnectRequested(object? sender, EventArgs e)
    {
        if (PromptBar is null || PromptInput is null) return;

        _prompt = PromptMode.Connect;

        PromptLabel.Text = "Connect to";

        // From the mounter, not from here: gio takes smb:// and the Windows
        // redirector takes \\server\share, and offering the wrong one is worse
        // than offering nothing.
        PromptInput.Text = _shell.ConnectPrefill;
        PromptInput.IsVisible = true;
        PromptConfirm.Content = "Connect";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = $"{_shell.ConnectHint} — esc to cancel";
        PromptBar.IsVisible = true;

        PromptInput.Focus();

        // Caret at the end, not a selection: the scheme is a starting point to
        // type after, not something to overwrite.
        PromptInput.CaretIndex = PromptInput.Text.Length;
    }


    /// <summary>
    /// Emptying the trash is the only action here with no undo AND no per-item
    /// review, so unlike trashing it is never unprompted — that is not a
    /// preference.
    /// </summary>
    /// <summary>
    /// Widens the window so a details panel has room.
    ///
    /// **The window manager has the final say and that is deliberate.** Asking
    /// Avalonia which screen this is on and clamping to its working area would
    /// mean an API this project has not verified, and getting it wrong is worse
    /// than letting the WM do what it already does correctly. If the screen
    /// cannot accommodate the request the window simply stops growing, and the
    /// panel stays hidden — `IsInfoVisible` is still set, so it appears the
    /// moment there is room.
    /// </summary>
    private void GrowToFit(double by)
    {
        if (by <= 0) return;

        // Maximised or full-screen windows cannot usefully be widened, and
        // trying would either do nothing or un-maximise them, which is a
        // surprising thing for a panel toggle to do.
        if (WindowState != WindowState.Normal) return;

        // Only the FIRST grow records the original. A second panel opening in a
        // split must not overwrite it, or closing both would restore to the
        // already-grown width instead of the one the user chose.
        _widthBeforeGrow ??= Width;

        // **The POSITION has to be remembered as well as the width.** Growing
        // pushes the right edge outward; when that runs past the screen the window
        // manager shoves the whole window LEFT to keep it visible. Shrinking the
        // width afterwards pulls the right edge back in but leaves the window
        // where the WM put it — so it lands well left of where it started, which
        // is exactly what "shooting to the left" was.
        _positionBeforeGrow ??= Position;

        Width += Math.Ceiling(by);

        // What we left it at, so a later release can tell whether the user has
        // resized in the meantime.
        _grownTo = Width;

        ViewModels.PaneGroupViewModel.PanelDebug($"[vaktari] panel: grew by {Math.Ceiling(by)} to {Width:F0} "
            + $"(original {_widthBeforeGrow:F0} at {_positionBeforeGrow?.X},"
            + $"{_positionBeforeGrow?.Y}; now at {Position.X},{Position.Y})");
    }

    /// <summary>The window's width before any panel grew it, if one did.</summary>
    private double? _widthBeforeGrow;

    /// <summary>The width this class last set, to detect a manual resize since.</summary>
    private double _grownTo;

    /// <summary>Where the window sat before any panel grew it.</summary>
    private PixelPoint? _positionBeforeGrow;

    /// <summary>
    /// Hands back the width taken for a details panel.
    ///
    /// **Refuses if the window is no longer the size we made it.** Someone who
    /// has dragged the edge since has expressed a preference, and snapping back to
    /// a width they last saw several actions ago would feel like the application
    /// fighting them. The recorded width is dropped in that case rather than kept,
    /// because it no longer describes anything the user would recognise.
    /// </summary>
    private void ReleaseGrownWidth()
    {
        // Every branch says WHY, because this feature has now taken three rounds
        // and "nothing happened" has four different causes that look identical.
        if (_widthBeforeGrow is not { } original)
        {
            ViewModels.PaneGroupViewModel.PanelDebug("[vaktari] panel: nothing to give back — the window was never grown");
            return;
        }

        var origin = _positionBeforeGrow;

        _widthBeforeGrow = null;
        _positionBeforeGrow = null;

        if (WindowState != WindowState.Normal)
        {
            ViewModels.PaneGroupViewModel.PanelDebug($"[vaktari] panel: not restoring — window is {WindowState}");
            return;
        }

        // A pixel of tolerance: the grow rounded up, and layout can settle a
        // fraction either way.
        if (Math.Abs(Width - _grownTo) > 1)
        {
            ViewModels.PaneGroupViewModel.PanelDebug($"[vaktari] panel: not restoring — width is {Width:F0} but we left "
                + $"it at {_grownTo:F0}, so it was resized by hand");
            return;
        }

        ViewModels.PaneGroupViewModel.PanelDebug($"[vaktari] panel: restoring {Width:F0} -> {original:F0}"
            + (origin is { } p && p != Position ? $", moving back to {p.X},{p.Y}" : ""));

        // WIDTH FIRST, then position. Narrowing makes the window fit again, so the
        // move that follows cannot trip the same off-screen correction that caused
        // the problem — doing it the other way round can bounce it straight back.
        Width = original;

        if (origin is { } home && home != Position) Position = home;
    }

    private void AskConfirmEmptyTrash()
    {
        if (PromptBar is null) return;
        if (_shell.ActiveTab is null) return;

        var held = ViewModels.PaneViewModel.Trash?.List() ?? [];
        if (held.Count == 0) { _shell.ActiveTab.Status = $"{Naming.TheBin} is already empty"; return; }

        _prompt = PromptMode.ConfirmEmptyTrash;

        PromptLabel.Text = ViewModels.Confirmations.EmptyBin(held);
        PromptInput.IsVisible = false;
        PromptConfirm.Content = $"Empty {Naming.BinName}";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "esc to cancel";
        PromptBar.IsVisible = true;

        PromptConfirm.Focus();
    }

    /// <summary>
    /// Deleting for good, from wherever it was asked for.
    ///
    /// **The bin is where a confirmed yes was refused.** Its rows carry the
    /// path the file USED to occupy, so they cannot go through the file
    /// operations at all — the trash's own key is the only safe route.
    ///
    /// One helper rather than the same three lines in two places, because the
    /// setting is a preference about ASKING and must not also decide WHICH
    /// deletion happens: with the confirmation turned off, the key took the
    /// branch that refuses in the bin while the menu row purged, and the two
    /// then meant different things on the same rows.
    /// </summary>
    private void PermanentlyDelete(PaneViewModel pane)
    {
        if (AppSettings.Current.General.ConfirmPermanentDelete) AskConfirmDelete();
        else if (pane.IsTrashListing) _ = pane.PurgeFromTrashAsync();
        else pane.DeleteSelectedCommand.Execute(null);
    }

    private void AskConfirmDelete()
    {
        if (PromptBar is null) return;
        if (_shell.ActiveTab is not { } pane) return;

        // **The entries, not just how many of them.** A count cannot name the
        // one thing being destroyed, and one thing is the case where naming it
        // costs nothing at all.
        var chosen = Chosen(pane);

        if (chosen.Count == 0) return;

        _prompt = PromptMode.ConfirmDelete;

        PromptLabel.Text = ViewModels.Confirmations.Delete(chosen);
        PromptInput.IsVisible = false;
        PromptConfirm.Content = "Delete permanently";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "esc to cancel";
        PromptBar.IsVisible = true;

        // Focus the button, not the bar: a focused Button takes Enter and Space
        // itself, which is a route nothing else can swallow.
        PromptConfirm.Focus();
    }

    /// <summary>
    /// Off by default, because trash is reversible and a prompt on a reversible
    /// action trains people to dismiss prompts. Dolphin offers it, so it is
    /// here for anyone who wants it.
    /// </summary>
    private void AskConfirmTrash()
    {
        if (PromptBar is null) return;
        if (_shell.ActiveTab is not { } pane) return;

        var chosen = Chosen(pane);

        if (chosen.Count == 0) return;

        _prompt = PromptMode.ConfirmTrash;

        PromptLabel.Text = ViewModels.Confirmations.MoveToBin(chosen);
        PromptInput.IsVisible = false;
        PromptConfirm.Content = $"Move to {Naming.TheBin}";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "esc to cancel";
        PromptBar.IsVisible = true;

        // Focus the button, for the same reason the delete prompt does: a
        // focused Button takes Enter and Space itself.
        PromptConfirm.Focus();
    }

    private void ClosePrompt()
    {
        _prompt = PromptMode.None;

        // The row's editor, which has no field here to hide: it is drawn by the
        // listing's item template, so putting it away is done through the pane
        // it belongs to. The PANE it was opened on rather than the active one —
        // the other side can be made active while a name is being typed, and
        // clearing the wrong pane would leave a box on a row nothing can close.
        if (_renamePane is { } renaming)
        {
            renaming.RenamingPath = "";
            renaming.RenameRefusal = null;

            // A GUARD, and no mutation can redden it: every route back into a
            // rename assigns _renamePane before it reads it, and the reads that
            // remain are all behind the `_prompt is Rename` test that the line
            // above this block has just failed. It is here so the window does
            // not keep a closed tab's pane alive, which is not something a test
            // in this suite can see.
            _renamePane = null;
        }

        if (PromptBar is not null) PromptBar.IsVisible = false;
        if (PromptInput is not null) PromptInput.IsVisible = false;
        if (PromptConfirm is not null) PromptConfirm.IsVisible = false;
        if (PromptCancel is not null) PromptCancel.IsVisible = false;

        // The rename box and every confirmation leave through here, and the
        // control they were focusing has just been hidden.
        FocusListingSoon();
    }

    /// <summary>
    /// Keeps the reason under the row's box honest as the name is typed.
    ///
    /// The reason arrives while the name is being typed, not only when Enter is
    /// pressed: a colon is refused the moment it appears, which is when it can
    /// be fixed without thinking about it.
    ///
    /// Through the pane's property rather than the box's TextChanged, because
    /// the box lives in a DataTemplate and there is no control here to
    /// subscribe to — the same reason the tapping and key handlers are hung on
    /// the window.
    /// </summary>
    private void OnRenameTyped(object? sender, PropertyChangedEventArgs e)
    {
        if (_prompt is not PromptMode.Rename) return;
        if (e.PropertyName != nameof(PaneViewModel.RenameText)) return;
        if (sender is not PaneViewModel pane || !ReferenceEquals(pane, _renamePane)) return;

        // The reason alone, and null while there is none. The bar's hint line
        // read "enter to confirm · esc to cancel" the rest of the time; held
        // open over a listing that is what a popup saying nothing would be.
        pane.RenameRefusal = Input.RenamePrompt.Decide(pane.RenameText, _renameTarget.Name).Reason;
    }

    /// <summary>
    /// Puts the row's editor away when the keyboard leaves it.
    ///
    /// **An inline editor that outlives its focus is litter.** The bar this
    /// replaces was docked at the window's edge and obviously a prompt; a box
    /// left sitting on a row halfway down the listing after you have clicked
    /// somewhere else reads as part of the listing. Cancelling rather than
    /// committing, matching Escape: a name nobody confirmed must not be applied
    /// by a click aimed at something else.
    ///
    /// Immediate, and it does not have to work out whether the keyboard is on
    /// its way to the NEXT box in a Tab run. Measured by listening for the same
    /// event during a step: the old box loses the keyboard SYNCHRONOUSLY, from
    /// inside <see cref="ClosePrompt"/>, on the line that clears RenamingPath
    /// and hides it — and ClosePrompt sets <c>_prompt</c> to None before that
    /// line, so the first guard below has already returned. The box's own
    /// Editing flag has gone false by then too, so the second guard would stop
    /// it as well. There is exactly one such event per step, and it never
    /// reaches the body.
    /// </summary>
    private void OnRenameBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_prompt is not PromptMode.Rename) return;
        if (e.Source is not TextBox box || !Input.RenameBox.GetEditing(box)) return;

        // **A box's own context menu is not somewhere else.** A TextBox carries
        // a Cut/Copy/Paste flyout, and opening it takes the keyboard — measured
        // here, a right press inside the editor closed the rename and left the
        // menu standing over a row with no box under it, which is the gesture
        // most likely to be wanted: pasting a name in. FocusBehavior makes the
        // same exception, for the same gesture, on the address bar.
        if (box.ContextFlyout is { IsOpen: true }) return;

        ClosePrompt();
    }

    /// <summary>
    /// Whether the keyboard is in the box a rename opened on a row.
    ///
    /// Both the key routing and the recovery above hang on this rather than on
    /// "a text box has focus": the path box and the filter are text boxes too,
    /// and a rename that has lost the keyboard to one of them has lost it.
    /// </summary>
    private bool RenameHasTheKeyboard()
        => FocusManager?.GetFocusedElement() is TextBox box && Input.RenameBox.GetEditing(box);

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (_prompt != PromptMode.Rename) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ConfirmPrompt();
                break;

            case Key.Escape:
                e.Handled = true;
                ClosePrompt();
                break;

            // **Renaming a run cost three keystrokes each — Enter, arrow, F2.**
            // Explorer answers Tab, which is how anybody who has tidied a
            // folder of photographs does it, and the arrow was the worst of the
            // three: a rename can re-sort the folder, so the row under the one
            // just finished is not the file that was under it a moment ago.
            // Two labels rather than one and a ternary, for two reasons. The
            // shortcuts sheet's cross-check reads case labels and their when
            // clauses, so this is what makes both gestures findable there. And
            // it leaves every other modifier alone: Ctrl+Tab means "next tab"
            // whether or not a name is being typed, and folding it into "not
            // Shift, so forward" would quietly take that away.
            case Key.Tab when e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                _ = StepRenameAsync(1);
                break;

            case Key.Tab when e.KeyModifiers == KeyModifiers.Shift:
                e.Handled = true;
                _ = StepRenameAsync(-1);
                break;
        }
    }

    /// <summary>
    /// Commits the name being typed and opens the next row's.
    /// </summary>
    private async Task StepRenameAsync(int step)
    {
        if (_shell?.ActiveTab is not { } pane) return;

        // **The neighbour is chosen BEFORE the rename lands.** Renaming
        // re-lists the folder and can re-sort it, so asked afterwards "the next
        // one" means whichever file has closed the gap behind the row just
        // finished — which is not the row anybody was looking at.
        // The rows on SCREEN. A run that started on a row from inside a folder
        // opened in place stopped dead against Entries: RenameRun.Next answers
        // null for a path the list it was given does not hold, so Tab did
        // nothing and said nothing.
        var next = Input.RenameRun.Next(pane.Rows, _renameTarget.FullPath, step);

        ConfirmPrompt();

        // Still open means the name was refused before it ever left this
        // window — a colon, a reserved name, nothing but spaces. The bar keeps
        // the text and says why, and stepping on would throw both away.
        if (_prompt is not PromptMode.None) return;

        if (next is not { } row) return;

        // **And the local check is only half of "did that rename happen".**
        // It answers the SHAPE of a name and never asks the disk, so the
        // commonest refusal in a run — the name is already taken — is not among
        // the ones caught above. It arrives on a continuation, into a status
        // line the next step's refresh would clear. Stepping past one is how a
        // run skips a file in silence.
        if (_lastRename is { } pending && !await pending) return;

        pane.SelectedEntry = row;
        OnRenameRequested(this, row);
    }

    // ---- input ---------------------------------------------------------

    /// <summary>
    /// What the desktop is set to, when it says so. Null means it did not, and
    /// this application's own default (double) applies. Set from the theme
    /// palette, which is re-read on startup, on a Plasma change, and on save.
    /// </summary>
    public static bool? SystemSingleClick { get; set; }

    /// <summary>
    /// Single click when the preference says so, or when it defers to a desktop
    /// that says so.
    /// </summary>
    private static bool OpensOnSingleClick
        => AppSettings.Current.Navigation.OpenItemsWith switch
        {
            ActivationClick.Single => true,
            ActivationClick.Double => false,
            _ => SystemSingleClick ?? false,
        };

    private string? _lastTapPath;

    private string? _lastOpenPath;
    private DateTime _lastOpenAt;

    /// <summary>
    /// The single place that opens, and it de-duplicates.
    ///
    /// TWO routes can each legitimately decide a row was double-clicked, and
    /// which one fires depends on whether Avalonia's gesture formed — so rather
    /// than bet on one, both call this and a repeat within the interval is
    /// dropped. Opening a folder twice is invisible; launching an application
    /// twice is not.
    /// </summary>
    private void TryOpen(FileEntry entry)
    {
        var now = DateTime.UtcNow;

        if (_lastOpenPath == entry.FullPath
            && now - _lastOpenAt < TimeSpan.FromMilliseconds(600))
            return;

        _lastOpenPath = entry.FullPath;
        _lastOpenAt = now;

        // **Forgotten once it has been acted on.** The row that was just opened
        // stayed remembered as "clicked once", so opening a folder, pressing
        // Back, and clicking that folder a single time to rename it entered it
        // again — the click before the double-click was still counting, minutes
        // later. There is deliberately no time limit on the pair, which is what
        // made the stale value reach so far.
        _lastTapPath = null;

        _ = _shell.ActiveTab?.OpenAsync(entry);
    }

    /// <summary>
    /// **Avalonia raises Tapped for the FIRST click and DoubleTapped for the
    /// second — it does not raise Tapped twice.** A previous attempt here
    /// counted two taps and therefore never fired when the gesture worked
    /// properly.
    ///
    /// So this is only the FALLBACK: it catches the case where the gesture does
    /// not form because the row's visual changed between clicks (selection
    /// swaps a ContentPresenter for a Border, which is why clicking the empty
    /// part of a line used to take four clicks while the icon and filename
    /// worked). DoubleTapped remains the normal path.
    /// </summary>
    private void OnTapped(object? sender, TappedEventArgs e)
    {
        // **A click in the row's own rename box counted as a click on the
        // row.** With the single-click preference on, one click to place the
        // caret opened the file being renamed; with it off, two did. The
        // remembered row is cleared as well, so a click before the rename
        // cannot pair with one aimed at the text.
        if (InRenameBox(e.Source))
        {
            _lastTapPath = null;
            return;
        }

        if (EntryAt(e.Source) is not { } entry) return;

        // **A modified click is a selection gesture and never an open.**
        // Ctrl+click to add a file to a selection LAUNCHED it in single-click
        // mode, and in double-click mode two Ctrl+clicks on the same row did —
        // so extending a selection opened whatever it passed over. Shift+click
        // to select a range did the same to the far end of the range.
        //
        // The remembered row is cleared as well as the open suppressed: the
        // second half of a two-click open must not be able to arrive from a
        // gesture that was never asking for one.
        if (e.KeyModifiers is not KeyModifiers.None)
        {
            _lastTapPath = null;
            return;
        }

        if (OpensOnSingleClick)
        {
            TryOpen(entry);
            return;
        }

        // [stated] the rule the user wants: clicking the same row twice opens
        // it, full stop. NO time limit — a 500 ms window meant a first click was
        // spent on selection and only a fast second one counted, so opening
        // something felt like select-then-double-click.
        //
        // Clicking a DIFFERENT row resets, which is what keeps this from firing
        // on anything you did not click twice in a row.
        if (_lastTapPath == entry.FullPath)
        {
            _lastTapPath = null;
            TryOpen(entry);
            return;
        }

        _lastTapPath = entry.FullPath;
    }


    /// <summary>
    /// The row's entry, found by walking UP from whatever was physically under
    /// the pointer.
    ///
    /// `e.Source` is the innermost visual, and the row template is several
    /// controls deep — a click on the filename lands on an `AccessText` whose
    /// DataContext is a `string`, not the `FileEntry`. Testing the source
    /// directly therefore only worked when the pointer happened to hit a
    /// control that carried the entry, which is why opening a folder could take
    /// four clicks: you were hunting for the right pixel. Double-click made it
    /// worse because it needs two qualifying hits in a row, not one.
    ///
    /// The same upward walk already exists in OnPointerPressedAnywhere for
    /// PaneGroupViewModel; this is that pattern, not a new idea.
    /// </summary>
    private static FileEntry? EntryAt(object? source)
    {
        // VISUAL tree, not Control.Parent.
        //
        // Parent is the LOGICAL parent, and a control generated inside a
        // template has no logical path back to the row that owns it — the
        // diagnostic showed `source=AccessText entry=NONE` even after the walk
        // was added, because AccessText lives inside the template and its
        // logical chain simply ends. The visual tree always connects, which is
        // why hit-testing questions belong there.

        // **A group heading is drawn INSIDE the row it stands over, and every
        // control in it inherits that row's FileEntry as its DataContext** — so
        // the walk below answered with the row for anything that landed on the
        // heading, and each caller took the one for the other. Measured on this
        // walk: a Button with the heading's class and a FileEntry DataContext
        // came back as that entry, exactly as a cell of the row does.
        //
        // Three call sites, all reachable with a single press: the left drag
        // arm is `EntryAt(e.Source) is not null`, so a twitch while pressing a
        // heading armed a drag of the row underneath it and a drop moved a real
        // file; and OnTapped and OnDoubleTapped open whatever this hands them,
        // so clicking a heading opened that row — on the first click, in
        // single-click mode.
        //
        // A separate walk rather than a test inside the loop below, and that is
        // not tidiness: the press lands on the presenter INSIDE the heading,
        // which carries the inherited FileEntry and not the class, so a check
        // in the loop would have answered with the row before ever reaching the
        // heading itself. Same shape and the same reason as ExpanderAt.
        if (GroupHeadingAt(source) is not null) return null;

        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: FileEntry entry }) return entry;
        }

        return null;
    }

    /// <summary>
    /// The group heading a press or a tap landed inside, or null for anything
    /// else.
    ///
    /// Stops at the ListBoxItem for the reason <see cref="ExpanderAt"/> gives:
    /// the walk ends inside the row it started in. That stop has NO KILLING
    /// MUTATION, exactly as ExpanderAt's does and for the same reason — nothing
    /// above a row carries the class, so the walk runs out either way — and it
    /// is here because a press on any other part of any row would otherwise
    /// climb to the window before answering null.
    /// </summary>
    internal static Control? GroupHeadingAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem) return null;

            if (visual is Control head && head.Classes.Contains(GroupHeadingClass)) return head;
        }

        return null;
    }

    /// <summary>
    /// True when the press landed inside the box a name is being typed in.
    ///
    /// **Two clicks in the editor opened the file.** <see cref="EntryAt"/>
    /// walks up to the first FileEntry DataContext and the rename box carries
    /// the row's own, so placing a caret and then double-clicking to select a
    /// word was an activation gesture: measured on this window, two clicks in
    /// the box renaming a folder called "adir" navigated into adir and left the
    /// rename pointing at what had become the current folder. With the
    /// single-click preference on, one click to place the caret was enough.
    /// </summary>
    private static bool InRenameBox(object? source)
    {
        // Visual tree, for the reason EntryAt gives: the press lands on the
        // TextPresenter inside the box's own template, which has no logical
        // path back out of it.
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is TextBox box && box.Classes.Contains(RenameBoxClass)) return true;
        }

        return false;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Selecting a word in the editor is not asking for the file to open.
        if (InRenameBox(e.Source)) return;

        // **Nothing happened on the blank half of the tab strip.** Both
        // references and every browser open a tab there.
        //
        // BEFORE the single-click branch below, deliberately: that preference
        // governs how a FILE is opened, and reading it first would have taken
        // this gesture away from everyone who opens files with one click — the
        // "+" beside the strip does not change meaning with that setting
        // either. Through the group rather than the shell, so in a split the
        // tab opens on the side that was double-clicked and not on the side
        // that happens to have focus.
        if (TabStripEmptySpaceAt(e.Source) is { } group)
        {
            group.NewTabHereCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // The normal path, restored. TryOpen drops a duplicate if the fallback
        // in OnTapped has already acted on this same row.
        if (OpensOnSingleClick) return;

        // Same rule as OnTapped: Ctrl+double-click is still a selection
        // gesture, and Explorer does not open on it either.
        if (e.KeyModifiers is not KeyModifiers.None) return;

        if (EntryAt(e.Source) is { } entry) TryOpen(entry);
    }

    /// <summary>
    /// The narrow set of keys that must be claimed before anything else sees
    /// them. Deliberately tiny: a tunnel handler runs ahead of every control in
    /// the window, so anything added here is taken away from all of them.
    /// </summary>
    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        // The box on the row answers Enter, Escape and Tab. On the TUNNEL for
        // the reason the path box below is: keyboard navigation claims Tab
        // before any bubble handler runs, so by then focus has already left the
        // box. The other two come with it rather than being hung on the
        // control, because the control is built by a DataTemplate and there is
        // no field here to subscribe to.
        if (_prompt is PromptMode.Rename && RenameHasTheKeyboard())
        {
            OnPromptKeyDown(sender, e);

            if (e.Handled) return;
        }

        if (PageCompactListing(e)) return;

        if (e.Key != Key.Tab || e.KeyModifiers != KeyModifiers.None) return;

        // Only while the path box is open and focused. Tab keeps its ordinary
        // meaning everywhere else, including the other text boxes.
        if (_shell.ActiveTab is not { IsPathEditing: true } pane) return;
        if (FocusManager?.GetFocusedElement() is not TextBox box) return;

        pane.CompletePathCommand.Execute(null);

        // Caret to the end, and the selection collapsed there, so the next
        // keystroke continues the path instead of landing wherever the caret
        // happened to sit — or worse, replacing a selection the text
        // replacement left behind.
        //
        // POSTED rather than set inline: the command assigns PathText, and the
        // binding has to propagate to this TextBox before its Text is the new
        // value. Setting CaretIndex now would measure against the OLD text and
        // get clamped to the wrong place.
        Dispatcher.UIThread.Post(() =>
        {
            var end = box.Text?.Length ?? 0;

            box.CaretIndex = end;
            box.SelectionStart = end;
            box.SelectionEnd = end;
        }, DispatcherPriority.Background);

        e.Handled = true;
    }

    /// <summary>
    /// Jump-to-letter in the listing. Bubble, so any control that wants the
    /// character has already had it.
    /// </summary>
    private void OnWindowTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(e.Text)) return;

        // Never while typing somewhere real — the filter bar, the path box and
        // the prompt bar are all TextBoxes, and stealing their characters would
        // be a far worse bug than not having type-ahead.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        // Control characters are not a search: Escape, Backspace and friends
        // arrive here too and have their own meanings.
        if (char.IsControl(e.Text[0])) return;

        if (_shell.ActiveTab is not { } pane) return;

        pane.TypeAhead(e.Text);
        e.Handled = true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_shell is null) return;

        // The prompt owns the keyboard while it is open.
        if (IsConfirming)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ConfirmPrompt();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ClosePrompt();
            }
            return;
        }

        if (_prompt is PromptMode.Rename)
        {
            // **An inline editor can lose the row it is drawn on.** The box is
            // built by the listing's item template, so scrolling its row out of
            // view unrealizes it — and nothing is then holding the keyboard,
            // while this guard would go on refusing every shortcut in the
            // window with nothing on screen to say why. A key pressed with the
            // keyboard out of the box ends the rename and is then handled as it
            // always was.
            if (RenameHasTheKeyboard()) return;

            ClosePrompt();
        }

        // F6 moves the keyboard between the listing, the address bar and the
        // sidebar.
        //
        // **It only ever went one place.** Explorer cycles three regions and
        // Dolphin's F6 is Replace Location; here it put the keyboard in the
        // listing and did nothing else — so pressed from the listing, which is
        // where it had just put you, it did nothing at all.
        //
        // **Above the text-box guard, deliberately.** Leaving a text box is
        // most of what this key is FOR: behind that guard the second step of
        // the cycle could never be taken, because the address bar is a text box
        // and F6 pressed in it would be swallowed. Three consequences follow,
        // each an accepted cost rather than an oversight — F6 from the path bar
        // discards a half-typed path, which is what clicking away already does;
        // F6 from the search field closes it when the draft is empty, which is
        // that field's own lost-focus rule; and F6 from the filter opens the
        // path bar with the filter text intact.
        //
        // The rename bar is a text box too, and F6 must not pull the keyboard
        // out from under a name being typed — but that is already answered by
        // the rename guard higher up, which returns before this is reached. No
        // clause of its own here, because one that cannot fail is one the next
        // reader has to work out is decorative.
        if (e.Key == Key.F6 && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            GoToRegion(Input.FocusCycle.Next(CurrentRegion(), SidebarShowing));
            return;
        }

        // The sidebar answers its own keys while it has the keyboard.
        //
        // **F6 delivered you to a panel you could not move in.** Up, Down, Home
        // and End were unbound anywhere in the application, so from a place row
        // the only way on was Tab through every button in the panel — and the
        // keys that WERE bound went on acting on the listing: Delete trashed
        // whatever was selected in a folder that no longer had the keyboard.
        //
        // **After F6 and above the text-box guard.** After F6, or the panel
        // this delivers you to would be one F6 could not take you out of; above
        // the guard is free, because there is no TextBox anywhere in the
        // sidebar, and putting it there keeps the two region branches together.
        if (CurrentRegion() == Input.KeyboardRegion.Sidebar)
        {
            // Written out here rather than mapped in SidebarWalk, one key to a
            // line: the shortcuts sheet is cross-checked against the keys this
            // handler claims, and it reads those claims from `e.Key == Key.X`.
            // A map behind a helper is a key nothing can see is bound.
            //
            // Modifiers must be absent rather than ignored — Shift+Down extends
            // a selection in a listing and means nothing here, and swallowing it
            // would take it from anything that later wants it.
            Input.SidebarStep? step =
                e.KeyModifiers != KeyModifiers.None ? null
                : e.Key == Key.Down ? Input.SidebarStep.Next
                : e.Key == Key.Up ? Input.SidebarStep.Previous
                : e.Key == Key.Home ? Input.SidebarStep.First
                : e.Key == Key.End ? Input.SidebarStep.Last
                : null;

            if (step is { } move)
            {
                e.Handled = true;
                MoveInSidebar(move);
                return;
            }

            // The Menu key opens the menu for the ROW, not for the listing.
            // Avalonia raises ContextRequested for a right-click and for
            // nothing else, so this is the only keyboard route into a place's
            // own menu — and the listing's menu, which is what these two keys
            // opened from here, acts on files that are not on screen.
            if (e.Key == Key.Apps
                || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
            {
                e.Handled = true;

                if (FocusManager?.GetFocusedElement() is Interactive row)
                    row.RaiseEvent(new ContextRequestedEventArgs());

                return;
            }

            // Refused rather than passed on. See SidebarWalk.ActsOnTheListing:
            // the selection these act on is not what the keyboard is pointing
            // at, and Delete is the one that costs files.
            if (Input.SidebarWalk.ActsOnTheListing(e.Key, e.KeyModifiers))
            {
                e.Handled = true;
                return;
            }
        }

        // The six gestures that could not be Window KeyBindings.
        //
        // **All six fired straight through an open rename bar.** A KeyBinding
        // is dispatched before this handler runs at all — before the key is
        // even routed — so the prompt guard at the top was structurally unable
        // to see them, the same fault F2 and Space were lifted out of the
        // markup for. Ctrl+H flipped the hidden files in behind the bar,
        // Ctrl+I opened the filter and pulled the caret into it so the rest of
        // the name was typed somewhere else, Ctrl+F swapped the listing for a
        // search, Ctrl+D pinned the folder, and Ctrl+Shift+N made a folder
        // whose own rename request the one-tenant rule then refused — so it was
        // created and left with the name the file system gave it.
        //
        // ABOVE the text-box guard, and a switch of its own because of it.
        // These are not gestures a text cursor owns, and two of them are
        // deliberately answered while one has focus: Ctrl+F from the path box
        // moves the keyboard to the search field on purpose, and Ctrl+I from
        // inside the filter box is how the filter is put away again. Behind the
        // guard — which is where the switch at the bottom of this method sits —
        // both would have been silently dropped, and the fix for a prompt bug
        // would have taken two working keys away.
        //
        // Case labels rather than `if`s, and `.XxxCommand.Execute(null)` rather
        // than the method: the shortcuts sheet is cross-checked against this
        // file, and it reads case labels and that call shape.
        switch (e.Key)
        {
            case Key.I when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                _shell.ActiveTab?.ToggleFilterCommand.Execute(null);
                return;

            case Key.N when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                e.Handled = true;
                _shell.ActiveTab?.NewFolderCommand.Execute(null);
                return;

            case Key.H when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                _shell.ActiveTab?.ToggleHiddenCommand.Execute(null);
                return;

            case Key.D when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                _shell.PinCurrentCommand.Execute(null);
                return;

            case Key.E when e.KeyModifiers == KeyModifiers.Control:
            case Key.F when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                _shell.ActiveTab?.BeginSearchCommand.Execute(null);
                return;
        }

        // Any focused text box owns the keyboard. Checking the type rather
        // than named controls, because the path and filter boxes now live
        // inside a per-pane template and have no generated fields — and it
        // is the more honest rule anyway. Escape and Enter inside those
        // boxes are handled by their own KeyBindings in the markup.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        // Space previews the selection.
        //
        // **Handled here rather than as a Window KeyBinding**, which is where it
        // lived. Markup KeyBindings are dispatched by the window's own key
        // handling, AHEAD of this handler — so every guard above was
        // structurally unable to save it, and typing a space while renaming a
        // file to "My Report" flipped a preview overlay open instead of typing
        // the space. The rename guard and the text-box guard now both apply,
        // because the gesture finally goes through them.
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            // **Not while a name is being typed.** Space is part of a filename
            // far more often than it is a shortcut: "new folder" toggled the
            // preview on the fourth keystroke and discarded the prefix, so
            // every two-word name in the folder was unreachable by typing.
            // Left unhandled, so the type-ahead handler downstream gets it.
            if (_shell.ActiveTab?.IsTypeAheadActive == true) return;

            e.Handled = true;
            _shell.ActiveTab?.TogglePreview();
            return;
        }

        // **The Menu key and Shift+F10 open the context menu**, which nothing
        // did: there was no Key.Apps handler, no F10, and no ContextRequested
        // raise anywhere — the only handler for that event SUPPRESSES the menu
        // after a right-drag. Avalonia does not provide this for free, so the
        // whole menu was mouse-only, and every keyboard route into it that the
        // shortcuts sheet implies simply did not exist.
        if (e.Key == Key.Apps
            || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
        {
            e.Handled = true;
            OpenListingMenu();
            return;
        }

        // **Escape did not close the preview**, which is the one thing on
        // screen that is drawn OVER the listing rather than beside it — and the
        // key everybody tries on a thing that covers something else. Space
        // reopens it, but Space is also how you got here, and a key that only
        // toggles is no help to someone who does not know that.
        //
        // First, and handled: the topmost dismissible thing goes first, and the
        // clear below is not something to do on the way past. Escape pressed to
        // put a preview away should not also throw away a filter the person is
        // still using.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
            && _shell.ActiveTab is { IsPreviewVisible: true } previewing)
        {
            e.Handled = true;
            previewing.TogglePreview();
            return;
        }

        // Escape abandons a pending cut, which the F1 sheet has promised all
        // along: "Escape — Clear the filter, and any pending cut".
        //
        // **It was reachable only from inside the filter box.** ClearFilter is
        // bound in exactly one place, that TextBox's own KeyBindings, and the
        // box is hidden unless the filter is open — so with a cut pending and
        // no filter showing, the key did nothing and the sheet was lying.
        // Not marked handled: Escape has other meanings further down, and this
        // one is harmless to whichever of them the user meant.
        //
        // DismissInListing rather than ClearFilter: the latter also closes the
        // bar once the text is empty, which is right from inside the box and
        // wrong from out here — it took away a bar the startup setting had
        // deliberately opened.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            _shell.ActiveTab?.DismissInListing();

        // Ctrl+1..9 jumps to a tab, browser-style.
        if (e.KeyModifiers == KeyModifiers.Control &&
            e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            e.Handled = true;
            _shell.SelectTabByIndex(e.Key - Key.D1);
            return;
        }

        // No Ctrl+arrow zoom. It was tried and removed: this handler is on the
        // bubble phase, so a focused ListBox — which is the normal state —
        // takes arrow keys first and moves the selection instead. Winning the
        // keystroke would mean tunnelling and stealing a key the listing has a
        // legitimate claim to. Ctrl+wheel and Ctrl +/- cover it.

        // Tab moves between sides rather than traversing focus, matching
        // Dolphin. Only when split, so it keeps its normal meaning otherwise —
        // and never while typing, or it would jump panes mid-edit.
        if (e.Key == Key.Tab && _shell.IsSplit && e.KeyModifiers == KeyModifiers.None
            && AppSettings.Current.General.TabSwitchesSplitPanes
            && FocusManager?.GetFocusedElement() is not TextBox)
        {
            e.Handled = true;
            _shell.FocusOtherPaneCommand.Execute(null);

            // **And move the keyboard with it.** The command only reassigns
            // which group is active, so focus stayed in the old ListBox: arrows
            // went on moving the OLD pane's selection while Enter, Delete and
            // Ctrl+C/X/V resolve through ActiveTab and acted on the NEW one.
            // Arrow to a file, press Delete, and the wrong file went to the bin.
            //
            // Posted rather than called: the listing for the other side is
            // chosen by ActiveTab, and that binding has not been applied yet at
            // this point in the keystroke.
            Dispatcher.UIThread.Post(() => ActiveListing()?.Focus());
            return;
        }

        if (_shell.ActiveTab is not { } pane) return;

        switch (e.Key)
        {
            // **Through the shell, not straight to the window.** Calling
            // ShowProperties() here went round the gate that keeps the sheet
            // out of the bin and Recent — both hold rows naming where a file
            // USED to be — so the menu entry was correctly greyed out while
            // Alt+Enter opened the sheet on a path that is not there.
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                e.Handled = true;
                _shell.ShowPropertiesCommand.Execute(null);
                break;

            case Key.Enter:
                e.Handled = true;
                _ = pane.OpenSelectedAsync();
                break;

            // Right opens the folder the keyboard is on without leaving this
            // one; Left shuts it again. Dolphin's keys for the same feature,
            // and the only route to it that is not a 16px triangle.
            //
            // **Handled only when it actually does something.** These two keys
            // belong to the grid and compact layouts, where the wrap panel
            // moves the selection sideways with them, and to any text box that
            // has the keyboard — the guard above has already returned for that
            // one. Left on a row that is not open, or either key in a layout
            // that has no triangles, falls straight through.
            //
            // Modifiers spelled out rather than ignored: Alt+Left is Back.
            case Key.Right when e.KeyModifiers == KeyModifiers.None:
                e.Handled = TurnExpansion(pane, open: true);
                break;

            case Key.Left when e.KeyModifiers == KeyModifiers.None:
                e.Handled = TurnExpansion(pane, open: false);
                break;

            // **Backspace answered Explorer's habit and nobody else's.** It
            // went Back through history, full stop — so somebody who learned
            // the key in Dolphin, where it goes to the parent folder, pressed
            // it at the bottom of a deep tree and was thrown to wherever they
            // had been ten minutes ago. A key that does nothing is a puzzle; a
            // key that confidently does the other thing is a wrong turn you
            // then have to undo.
            //
            // A preference rather than a choice made for everybody, because
            // both habits are real and neither is wrong. Back is the default:
            // it is what shipped, and it is what the larger audience expects.
            //
            // Read at the keystroke rather than captured at startup, so the
            // setting takes effect the moment Save is pressed. Alt+Left and
            // Alt+Up are untouched by this and still do their own jobs either
            // way, so flipping it costs no route.
            case Key.Back:
                e.Handled = true;

                _ = AppSettings.Current.Navigation.BackspaceGoesUp
                    ? pane.GoUpAsync()
                    : pane.GoBackAsync();

                break;

            // Rename, and rename in bulk.
            //
            // **F2 re-entered the rename bar that was already open.** Both were
            // Window KeyBindings, and a KeyBinding is dispatched ahead of this
            // handler — so the prompt guard at the top was structurally unable
            // to see them. Pressing F2 again discarded the name being typed and
            // re-pointed the bar at the listing's CURRENT selection, which is a
            // different file the moment another row has been clicked: the bar
            // is inline, non-modal, and the listing behind it stays live.
            // Shift+F2 opened the batch dialog over the top of the open bar.
            //
            // Handled here, both sit behind the prompt guard and behind the
            // rule that a focused text box owns the keyboard — so F2 while the
            // address or filter box has focus no longer renames a row hidden
            // behind that box. The modifiers are spelled out on both arms so
            // the pair matches exactly the two gestures the markup bound and
            // nothing more.
            case Key.F2 when e.KeyModifiers == KeyModifiers.Shift:
                e.Handled = true;
                _shell.BatchRenameCommand.Execute(null);
                break;

            case Key.F2 when e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                pane.BeginRenameCommand.Execute(null);
                break;


            // Delete trashes, which is recoverable. Shift+Delete is
            // irreversible. Both prompts are now preferences, but they default
            // the way they always behaved: trash silently, confirm the
            // permanent one.
            // Ctrl+A had no equivalent anywhere in the application — a file
            // manager with rubber-band selection and no select-all. Routed
            // through the ListBox rather than the view model so the framework's
            // bulk path does the work: filling the bound collection item by item
            // would fire CollectionChanged once per file, and each one refreshes
            // the details panel and recomputes the summary.
            case Key.A when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                if (FocusManager?.GetFocusedElement() is TextBox) break;

                InvertSelection();
                e.Handled = true;
                break;

            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                if (FocusManager?.GetFocusedElement() is TextBox) break;

                if (ActiveListing() is { } everything) SelectWholeFolder(everything, pane);

                e.Handled = true;
                break;

            case Key.Delete when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                PermanentlyDelete(pane);
                break;

            case Key.Delete:
                e.Handled = true;

                if (AppSettings.Current.General.ConfirmMoveToTrash)
                    AskConfirmTrash();
                else
                    pane.TrashSelectedCommand.Execute(null);

                break;

            // **The only place the clipboard is bound.** These three were also
            // Window.KeyBindings, and a KeyBinding is dispatched ahead of this
            // handler — so the text-box guard above could never save them and
            // the address bar could not copy or paste. Ctrl+V pasted FILES into
            // the folder behind the box; Ctrl+C replaced the system clipboard
            // with the listing's selection, destroying the path you were about
            // to paste; Ctrl+X armed a move of it. Handled here, the guard
            // applies and a focused TextBox keeps its own keys.
            case Key.C when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                pane.CopySelectionToClipboardCommand.Execute(null);
                break;

            case Key.X when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                pane.CutSelectionToClipboardCommand.Execute(null);
                break;

            case Key.V when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                pane.PasteCommand.Execute(null);
                break;

            // Undo and redo of FILE operations, and they moved here for a
            // sharper reason than the clipboard did: as Window.KeyBindings,
            // pressing Ctrl+Z to take back a mistyped character in the address
            // bar reversed the last copy, move or delete on disk instead. A
            // text box gets its own undo stack back, and this one now only
            // fires when the keyboard is in the listing.
            //
            // Shift before the plain one: KeyModifiers is a flags enum and
            // Ctrl+Shift+Z would otherwise never be reached.
            case Key.Z when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
            case Key.Y when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                pane.RedoCommand.Execute(null);
                break;

            case Key.Z when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                pane.UndoCommand.Execute(null);
                break;
        }
    }
}
