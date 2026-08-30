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
    private readonly Vaktari.Core.FileSystem.IApplicationLauncher? _launcher;
    private readonly IPlatform _platform;

    /// <summary>
    /// Chooses which icon set the listings use.
    ///
    /// **An imported theme outranks the platform own set**: it is the most
    /// deliberate of the three sources — somebody found a theme, downloaded it
    /// and pointed at it. A folder that has since been moved or deleted is
    /// ignored rather than honoured into a listing with no icons at all.
    ///
    /// Called after the settings are applied, and again when they are saved.
    /// </summary>
    /// <summary>
    /// The chosen theme, applied when it is ready rather than before the window
    /// is allowed to open. <see cref="Thumbnails.IconThemeInstall"/> carries the
    /// measurements and what changes on screen as a result.
    /// </summary>
    private static void InstallIconTheme(IPlatform platform)
    {
        // Before anything reads a theme. A null folder disables caching, which
        // means paying the build on every launch rather than only the first.
        Vaktari.Core.FileSystem.FreedesktopIconTheme.IndexCacheFolder =
            Path.Combine(JsonSessionStore.DefaultDirectory(), "icon-index");

        Thumbnails.IconThemeInstall.Begin(
            AppSettings.Current.General.IconThemeFolder,
            platform.Icons,
            folder => Vaktari.Core.FileSystem.FreedesktopIconTheme.FromCache(folder),
            folder => Vaktari.Core.FileSystem.FreedesktopIconTheme.FromFolder(folder),
            UseIconProvider);

        // Housekeeping, once per launch, off the thread. An index is sixteen
        // megabytes for a big theme and nothing else ever removes one whose
        // theme was deleted; the active theme's entry records a directory that
        // exists, so the prune never touches it.
        _ = Task.Run(Vaktari.Core.FileSystem.FreedesktopIconTheme.PruneIndexCache);
    }

    /// <summary>
    /// Marshals to the UI thread itself rather than asking the caller to. The
    /// first call arrives on it and the second does not, and a Post from the UI
    /// thread would defer the platform's icons past first paint for no reason.
    /// </summary>
    private static void UseIconProvider(Vaktari.Core.FileSystem.IIconThemeProvider? provider)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UseIconProvider(provider));
            return;
        }

        Thumbnails.IconLoader.Provider = provider;

        // Resolved paths and drawables belong to whatever was in place before.
        // On the first call there are none; on the swap they are the shell's,
        // and every one of them is now the wrong picture.
        Thumbnails.IconLoader.Invalidate();
    }
    private readonly JsonSessionStore _store;

    // Preferences, as distinct from the session. Read before it, because the
    // startup setting decides whether the session is consulted at all.
    private readonly JsonSettingsStore _settingsStore;
    private readonly SettingsState _settings;

    private JsonFolderViewStore? _folderViews;
    private Vaktari.Core.Sharing.ProtonDriveLinks? _driveLinks;
    private JsonDriveLinkStore? _driveLinkStore;
    private JsonRecentStore? _recents;
    private ITrashMaintenance? _trashMaintenance;
    private DispatcherTimer? _trashTimer;
    private readonly IDefaultFileManager? _defaultFileManager;
    private readonly IPropertiesProvider _properties;
    private readonly IThemeProvider? _theme;
    private readonly IAccessEditor? _accessEditor;
    private bool _closeApproved;

    public MainWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);

        // The one and only place a platform type is named, and the one guard
        // the analyser needs — each platform assembly is annotated for a single
        // OS, so everything inside them is free of per-call checks.
        //
        // The #if is not belt-and-braces around the runtime check. Only one
        // platform assembly is referenced per build (see Vaktari.Ui.csproj), so
        // the branch for the other OS would not compile at all. The runtime
        // check still earns its place: it is what the analyser reads to allow
        // the constructor call.
        const string Unsupported =
            "No platform implementation for this operating system yet.";

        IPlatform platform;

        // Each branch carries its own else rather than sharing one after the
        // #endif. Sharing it compiled, but left the #else arm as a dangling
        // `else` — so a build with neither symbol reported five cascading syntax
        // errors and buried the #error that explains what actually went wrong.
#if VAKTARI_LINUX
        if (OperatingSystem.IsLinux())
            platform = new LinuxPlatform(JsonSessionStore.DefaultDirectory());
        else
            throw new PlatformNotSupportedException(Unsupported);
#elif VAKTARI_WINDOWS
        if (OperatingSystem.IsWindows())
            platform = new WindowsPlatform(JsonSessionStore.DefaultDirectory());
        else
            throw new PlatformNotSupportedException(Unsupported);
#else
#error Vaktari.Ui references no platform assembly. One is selected from the build machine's OS, or by -p:VaktariPlatform=Linux|Windows; see Vaktari.Ui.csproj and WINDOWS.md §2.
        platform = null!;
#endif

        // Before anything builds a label. The view models below read these, and
        // so does every prompt sentence — adopting it here, beside the one place
        // a platform is chosen, is what keeps the window from naming the bin
        // two different ways.
        Naming.Adopt(platform);
        _defaultFileManager = platform.DefaultFileManager;

        // Clears a folder-handler registration left by a previous name of this
        // application pointing at a binary that no longer exists. Upgrading
        // from Heimdall removes that install and leaves its verb behind, after
        // which every double-clicked folder fails — the same wound the
        // uninstaller avoids, reached by a different route. Acts only when the
        // command is genuinely dead, so a Heimdall someone still runs is left
        // alone.
        platform.DefaultFileManager?.HealPreviousName();

        _properties = platform.Properties;
        _accessEditor = platform.AccessEditor;

        Thumbnails.ThumbnailLoader.Provider = platform.Thumbnails;
        Thumbnails.RowMetadata.Provider = platform.Metadata;
        // The desktop's own per-file icons, used only if the setting asks for
        // them. Installed regardless so the choice can be changed without a
        // restart.
        Thumbnails.IconLoader.Files = platform.FileIcons;

        // Held for the settings dialog, which opens a browser at the icon
        // catalogue. The launcher is how this application opens anything.
        _launcher = platform.Launcher;
        _platform = platform;

        if (platform.Icons is { } icons)
        {
            var probe = icons.Resolve(["inode-directory", "folder"], 32);
            Console.Error.WriteLine($"[vaktari] folder icon resolved to: {probe ?? "NOTHING"}");
        }

        // Settings BEFORE the theme, and this ordering is load-bearing rather
        // than tidy. ThemeApplier reads AppSettings.Current to decide whether a
        // configured font beats Plasma's — so loading settings afterwards meant
        // it read empty defaults and the font setting did nothing at all, even
        // across a restart. Settings are the first thing this constructor does
        // for the same reason they precede the session load below.
        _settingsStore = new JsonSettingsStore(JsonSessionStore.DefaultDirectory());
        _settings = _settingsStore.Load();
        _settingsStore.EnsureFileExists(_settings);

        AppSettings.Apply(_settings);

        // **After Apply, and that ordering is the whole of it.** This was
        // written thirty lines above, where AppSettings.Current is still the
        // default record and the chosen folder is therefore always empty — so
        // FromFolder returned null on every launch and the imported theme was
        // never used by anybody, on either platform, while settings.json
        // faithfully recorded the choice and the dialog showed it as set. The
        // comment below about settings preceding the theme is about this exact
        // hazard; the font setting was caught by it once already.
        InstallIconTheme(platform);

        // Per-folder view overrides. A static for the same reason AppSettings is
        // one: panes are created by the shell, not injected here.
        _folderViews = new JsonFolderViewStore(JsonSessionStore.DefaultDirectory());
        ViewModels.PaneViewModel.FolderViews = _folderViews;

        _recents = new JsonRecentStore(JsonSessionStore.DefaultDirectory());
        ViewModels.PaneViewModel.Recents = _recents;

        // Platform-neutral: it drives the `git` binary, which behaves the same
        // on both targets, so it is constructed here rather than coming from
        // IPlatform like the trash and the icon theme do.
        ViewModels.PaneViewModel.Vcs = new Vaktari.Core.Vcs.GitVersionControl();

        // Same reasoning, different binary: Proton's own CLI, which behaves the
        // same on both targets and carries the encryption itself.
        // The setting always wins; the guess covers the machine where the
        // Proton Drive app is installed and nobody has told Vaktari where —
        // which should cost a click, not a treasure hunt through settings.
        _driveLinks = new Vaktari.Core.Sharing.ProtonDriveLinks
        {
            LocalRoot = AppSettings.Current.General.ProtonDriveFolder is { Length: > 0 } chosen
                ? chosen
                : Vaktari.Core.Sharing.ProtonDriveLinks.GuessLocalRoot() ?? "",
        };
        _driveLinkStore = new JsonDriveLinkStore(JsonSessionStore.DefaultDirectory());

        // From the platform, unlike the one above: what a desktop puts on a
        // context menu is entirely a platform fact, and on Linux the answer is
        // that there is no such thing.
        ViewModels.PaneViewModel.ShellMenu = platform.ShellMenu;
        _virtualDrop = platform.VirtualFileDrop;
        _shortcuts = platform.Shortcuts;

        // Logged at startup, not when the settings dialog opens. The count only
        // appeared on opening the dialog, which made "no line printed" mean two
        // different things and cost a diagnostic round trip. Compare with:
        //   fc-list : family | tr ',' '\n' | sort -u | wc -l
        Console.Error.WriteLine(
            $"[vaktari] fontlist: {Avalonia.Media.FontManager.Current.SystemFonts.Count} "
            + "families visible to Avalonia");

        // Applied before anything else paints, and re-applied whenever Plasma's
        // scheme changes, so the window follows the desktop live.
        _theme = platform.Theme;
        var platformIcons = platform.Icons;
        ThemeApplier.Apply(this, _theme?.Read());

        if (_theme is not null)
        {
            _theme.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
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
        }

        // Not platform-specific: the clipboard comes from the toolkit.
        IClipboardService clipboard = ClipboardService.ForWindow(this);

        _store = new JsonSessionStore(JsonSessionStore.DefaultDirectory());

        // Loaded synchronously so geometry is applied before first paint. An
        // async load would restore size and position after the window is
        // already on screen — a visible jump on every launch.
        var state = _store.Load();
        ApplyGeometry(state);

        _shell = new ShellViewModel(
            platform.FileSystem, platform.Operations, _store,
            platform.Places, platform.Launcher, clipboard, platform.Search,
            platform.Scripts, platform.Templates, platform.Sharing)
        {
            GeometryProvider = CaptureGeometry,
        };
        _shell.PaneCreated += (_, pane) => WirePane(pane);
        _shell.PropertiesRequested += (_, _) => ShowProperties();
        _shell.SettingsRequested += (_, _) => ShowSettings();
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

        if (_driveLinks is not null && _driveLinkStore is not null)
            _shell.UseDriveLinks(
                _driveLinks, _driveLinkStore.Load(), links => _driveLinkStore.Save(links),
                url => _launcher?.Open(url));
        _shell.UseDiscovery(platform.Discovery);
        _shell.UseProperties(platform.Properties);

        _shell.ConnectionInfoRequested += (_, info) =>
            new ConnectionWindow(info).ShowDialog(this);

        _shell.ShortcutsRequested += (_, _) => new ShortcutsWindow().ShowDialog(this);

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

                // ShowDialog returns when the window closes; the window closes
                // when the model answers, and closing it any other way answers
                // Cancel. So this cannot wait forever on a dismissed dialog.
                await new ConflictWindow(model).ShowDialog(this);

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

        PromptInput.KeyDown += OnPromptKeyDown;
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
        AddHandler(PointerReleasedEvent, (_, _) => EndBand(), RoutingStrategies.Tunnel);

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

        // A folder named on the command line, and any handed over by a later
        // launch. Without this the window ignored the path it was asked for,
        // which as a default file manager is the whole job.
        if (Program.Instance is { } instance)
            instance.PathsReceived += (_, paths) => OpenPaths(paths, activate: true);

        if (Program.StartupPaths.Length > 0)
            Dispatcher.UIThread.Post(() => OpenPaths(Program.StartupPaths, activate: false));

        Closing += OnClosing;
        Resized += (_, _) => _shell.NotifyWindowChanged();
        PositionChanged += (_, _) => _shell.NotifyWindowChanged();

        // Applied before Start so the first paint is already at the right size.
        var geometry = state?.Windows.FirstOrDefault();
        ApplyScales(
            geometry?.FontScale is > 0 and var f ? f : 1.0,
            geometry?.IconScale is > 0 and var i ? i : 1.0);

        // The startup setting decides whether the session is consulted at all,
        // which is the whole reason settings are loaded before it. Restoring
        // stays the default: forgetting open folders is the complaint this
        // project exists to answer.
        var startup = _settings.Startup;

        var restore = startup.ShowOnStartup == StartupLocation.RestoreSession;

        var openFolder = startup.ShowOnStartup switch
        {
            StartupLocation.SpecificFolder when
                !string.IsNullOrWhiteSpace(startup.StartupFolder)
                && Directory.Exists(startup.StartupFolder) => startup.StartupFolder,

            // A configured folder that no longer exists falls back to home
            // rather than opening nothing — an unremovable empty window would
            // be a worse failure than quietly ignoring a stale path.
            _ => null,
        };

        _shell.Start(restore ? state : null, openFolder);

        ApplyStartupPreferences(startup);

        StartTrashMaintenance(platform.TrashMaintenance);

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
    private static ListBox? ListForEmptySpace(object? source)
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

            if (visual is ListBoxItem hit) { row = hit; continue; }

            if (visual is not ListBox found) continue;

            // Not every list can hold a multiple selection — the column strip is
            // SelectionMode="None", and a band there would draw a rectangle that
            // selects nothing.
            if (!found.SelectionMode.HasFlag(SelectionMode.Multiple)) return null;

            // Nothing under the pointer but the list itself: always a band.
            if (row is null) return found;

            // The press landed on a row's BACKGROUND. Allow a band only where the
            // row spans the list, because then there is no empty space beside it
            // and the background is the only place left to start one. A tile
            // leaves gaps of its own, and stealing its background would make
            // dragging a file out of the grid needlessly fiddly.
            return row.Bounds.Width >= found.Bounds.Width * 0.9 ? found : null;
        }

        return null;
    }

    private static void FocusListIfEmptySpace(object? source)
    {
        // Without this, pressing Home or End after clicking below the tiles did
        // nothing: keyboard navigation begins at the focused element.
        if (ListForEmptySpace(source) is { IsFocused: false } list) list.Focus();
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
        // Claimed on the tunnel, before the listing sees the press. Some
        // controls treat any pointer press as a selection gesture, and a
        // side button would then move the selection as well as the folder.
        if (Input.SideButtons.For(properties.PointerUpdateKind) is var side
            && side is not Input.SideButtonAction.None)
        {
            var pane = PaneAt(e.Source) ?? _shell.ActiveTab;

            if (pane is not null)
            {
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

            if (FolderRowAt(e.Source) is { } folder)
            {
                _shell.OpenInNewTab(folder);
                e.Handled = true;
                return;
            }

            // Anywhere else it means nothing, and is left alone rather than
            // swallowed — a middle click on the listing background is how some
            // desktops paste.
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
        FocusListIfEmptySpace(e.Source);

        // Recorded here so a drag can start on the first move past the
        // threshold rather than on the press itself.
        _dragOrigin = e.GetPosition(this);

        // **A drag from empty space is a SELECTION, not a file drag.** Both
        // begin with a left press inside a pane, so the only thing separating
        // them is what sat under the pointer — and arming both would race.
        _bandList = properties.IsLeftButtonPressed ? ListForEmptySpace(e.Source) : null;
        _bandOrigin = e.GetPosition(BandLayer);
        _bandAdditive = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                        || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _bandKept = null;

        // **The right button drags too**, which is Explorer's oldest answer to
        // "did I just move that or copy it": drag with the right button and a
        // menu asks at the drop. Only from a row — a right press on empty
        // space keeps meaning the background menu.
        _dragRight = properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed
                     && EntryAt(e.Source) is not null;

        _dragSource = (properties.IsLeftButtonPressed && _bandList is null) || _dragRight
            ? PaneAt(e.Source)
            : null;
        _dragTrigger = _dragSource is null ? null : e;

        // Visual tree — same reason as EntryAt. A press that lands on
        // templated content has no logical path back to the group, so clicking
        // a filename would not activate its side.
        for (var visual = e.Source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PaneGroupViewModel group })
            {
                _shell.ActivateGroup(group);
                break;
            }
        }
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
        var model = new BatchRenameViewModel(entries,
            (entry, name) => pane.RenameOrThrowAsync(entry, name),
            pane.Entries);

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
        var model = new SettingsViewModel(AppSettings.Current, _defaultFileManager);

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
            if (_driveLinks is not null)
                _driveLinks.LocalRoot =
                    model.Result.General.ProtonDriveFolder is { Length: > 0 } chosen
                        ? chosen
                        : Vaktari.Core.Sharing.ProtonDriveLinks.GuessLocalRoot() ?? "";

            // Rebuilt on save, or choosing a theme would need a restart — and
            // the resolved-path cache has no theme in its key, so it has to be
            // dropped or it keeps serving files from the theme just abandoned.
            InstallIconTheme(_platform);
            Thumbnails.IconLoader.Invalidate();
            _settingsStore.Save(model.Result);

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

            Title = model.Result.Startup.ShowFullPathInTitleBar
                    && _shell.ActiveTab is { } pane
                ? $"{pane.CurrentPath} — Vaktari"
                : "Vaktari";
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
    /// contents. If this menu ever gains an entry that applies to any place,
    /// this handler is what has to go.
    /// </summary>
    private void OnPlaceMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is Control { DataContext: PlaceItemViewModel { IsUserPinned: true } }) return;

        e.Cancel = true;
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
            || Find<MenuItem>(menu.Items, "ProtonInstallItem") is not { } install
            || Find<MenuItem>(menu.Items, "ProtonInstallingItem") is not { } installing
            || Find<Separator>(menu.Items, "ShareMethodSeparator") is not { } separatorHost) return;

        var entry = (menu.DataContext as ViewModels.PaneGroupViewModel)?.ActiveTab?.SelectedEntry;
        var path = entry?.FullPath;

        var linkable = path is not null && _shell.CanLinkShare(path);
        var existing = path is not null ? _shell.LinkFor(path) : null;

        share.IsVisible = linkable && existing is null;
        copy.IsVisible = linkable && existing is not null;
        unshare.IsVisible = linkable && existing is not null;

        // The tool missing is a state of the feature, not its absence: for an
        // item the drive folder covers, the slot the share row would occupy
        // offers the install instead, and shows the download's progress row
        // while it runs.
        var installable = path is not null && _shell.CanOfferDriveInstall(path);
        var busy = path is not null && _shell.ShowDriveInstallBusy(path);

        install.IsVisible = installable;
        installing.IsVisible = busy;

        // The submenu earns its place when EITHER way of sharing applies; the
        // rule between them only when both do.
        var proton = linkable || installable || busy;

        shareMenu.IsVisible = proton || _shell.HasSharingEntry;
        separatorHost.IsVisible = proton && _shell.HasSharingEntry;
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

    private void OnProtonInstallClicked(object? sender, RoutedEventArgs e)
        => _shell.InstallDriveLinksCommand.Execute(null);

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

    private Point _dragOrigin;
    private PaneViewModel? _dragSource;
    private bool _dragging;

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

        if (_dragging || _dragSource is null) return;

        var held = e.GetCurrentPoint(this).Properties;

        if (!(_dragRight ? held.IsRightButtonPressed : held.IsLeftButtonPressed))
        {
            _dragSource = null;
            _dragTrigger = null;
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

        var rect = new Rect(
            Math.Min(_bandOrigin.X, here.X), Math.Min(_bandOrigin.Y, here.Y),
            Math.Abs(here.X - _bandOrigin.X), Math.Abs(here.Y - _bandOrigin.Y));

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

        foreach (var container in Rows(list))
        {
            if (container.DataContext is not { } item) continue;

            // TranslatePoint rather than a stored offset: rows move as the list
            // scrolls, and a cached position would select the wrong ones the
            // moment it did.
            if (container.TranslatePoint(default, BandLayer) is not { } origin) continue;

            var bounds = new Rect(origin, container.Bounds.Size);

            if (bounds.Intersects(rect) && !wanted.Contains(item)) wanted.Add(item);
        }

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
        SelectionBand.IsVisible = false;
    }

    private async Task BeginDragAsync(PaneViewModel pane, PointerPressedEventArgs trigger)
    {
        var paths = pane.Selection.Count > 0
            ? pane.Selection.Select(x => x.FullPath).ToList()
            : pane.SelectedEntry is { } one ? [one.FullPath] : [];

        if (paths.Count == 0) return;
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
        var place = PlaceAt(e.Source);

        if (place is null && PaneAt(e.Source) is null)
        {
            e.DragEffects = DragDropEffects.None;
            HighlightDropTarget(null);
            return;
        }

        var pane = PaneAt(e.Source);
        var destination = place ?? FolderRowAt(e.Source) ?? pane?.CurrentPath ?? "";

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
            HighlightDropTarget(place is null ? pane : null);
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
        // the wrong half of the window.
        HighlightDropTarget(place is null ? pane : null);
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
                foreach (var inside in Directory.EnumerateFiles(
                             path, "*", SearchOption.AllDirectories))
                {
                    files++;
                    try { bytes += new FileInfo(inside).Length; } catch { }
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

    private void OnDragLeave(object? sender, DragEventArgs e) => HighlightDropTarget(null);

    private void HighlightDropTarget(PaneViewModel? pane)
    {
        foreach (var group in new[] { _shell.Left, _shell.Right })
        {
            if (group is null) continue;
            foreach (var tab in group.Tabs) tab.IsDropTarget = ReferenceEquals(tab, pane);
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        HighlightDropTarget(null);

        // A sidebar place is a destination in its own right, and has no pane
        // above it to ask about.
        var place = PlaceAt(e.Source);
        var pane = PaneAt(e.Source) ?? (place is null ? null : _shell.ActiveTab);

        if (pane is null) return;

        // Dropping onto a folder row means into that folder, not into the
        // directory being listed — that is what the pointer was over.
        var target = place ?? FolderRowAt(e.Source);
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

    private void ApplyGeometry(SessionState? state)
    {
        if (state?.Windows.FirstOrDefault() is not { } w) return;

        if (w.Width > 200) Width = w.Width;
        if (w.Height > 200) Height = w.Height;

        if (w.X != 0 || w.Y != 0)
            Position = new PixelPoint((int)w.X, (int)w.Y);

        if (w.IsMaximized)
            WindowState = Avalonia.Controls.WindowState.Maximized;
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
    /// Opens folders in tabs. Files resolve to the folder holding them, because
    /// "open containing folder" is the request the desktop actually sends.
    /// </summary>
    private void OpenPaths(IReadOnlyList<string> paths, bool activate)
    {
        foreach (var raw in paths)
        {
            var path = raw;

            if (File.Exists(path) && Path.GetDirectoryName(path) is { Length: > 0 } parent)
                path = parent;

            if (!Directory.Exists(path)) continue;

            _shell.OpenInNewTab(path);
        }

        if (!activate) return;

        // Raise the existing window: the user asked to see a folder, and
        // silently loading it behind whatever they were doing is not that.
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved) return;

        // Asked before anything is torn down, and only when there is something
        // to lose. Off by default: the session is restored on next launch, so
        // closing a window full of tabs is not actually destructive here — which
        // is exactly why this is a preference rather than the behaviour.
        if (AppSettings.Current.General.ConfirmClosingMultipleTabs && CountOpenTabs() > 1)
        {
            e.Cancel = true;

            var confirmed = await ConfirmCloseAsync();
            if (!confirmed) return;
        }

        // Cancel, flush, then close for real. Awaiting inside an async void
        // handler does not hold the window open — the process can otherwise
        // exit with the write still in flight.
        e.Cancel = true;

        // Two independent concerns, so two try blocks. They were one, sequenced
        // shares-first: a throw from StopAllSharesAsync then skipped the flush
        // AND the dispose, and the single catch still printed "session flush
        // failed" for a flush that had never been attempted.
        //
        // Session goes first now. It is the one whose loss the user would
        // actually notice, and it cannot fail because of a subprocess.
        try
        {
            _folderViews?.Flush();
            _recents?.Flush();
            await _store.FlushAsync(CancellationToken.None);
            await _store.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] session flush failed: {ex.Message}");
        }

        try
        {
            // Servers we started are ours to stop; a share outliving the window
            // would keep a folder on the network with nothing showing it.
            await _shell.StopAllSharesAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] stopping shares failed: {ex.Message}");
        }

        _closeApproved = true;
        Close();
    }

    /// <summary>
    /// Startup preferences that act on the window once it exists. Separate from
    /// the restore decision above because these apply whether or not a session
    /// was restored.
    /// </summary>
    private void ApplyStartupPreferences(StartupSettings startup)
    {
        Title = startup.ShowFullPathInTitleBar && _shell.ActiveTab is { } titled
            ? $"{titled.CurrentPath} — Vaktari"
            : "Vaktari";

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
    private async Task<bool> ConfirmCloseAsync()
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

        var close = new Button { Content = "close anyway", Padding = new Thickness(14, 4) };
        var cancel = new Button { Content = "cancel", Padding = new Thickness(14, 4) };

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
                    Text = $"{CountOpenTabs()} tabs are open. Close anyway?",
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

    /// <summary>
    /// Trash expiry, at startup and then hourly.
    ///
    /// Hourly rather than on a shorter tick because nothing here is urgent —
    /// a trash that is one hour over its age limit is not a problem — and
    /// because each sweep walks the trash to size it, which is real work to be
    /// doing behind someone's back.
    /// </summary>
    private void StartTrashMaintenance(ITrashMaintenance? maintenance)
    {
        if (maintenance is null) return;

        _trashMaintenance = maintenance;

        // Assigned HERE, not beside the other providers in the constructor:
        // this field is null until this method runs, so the earlier assignment
        // handed the pane a null and the Trash listing would have been silently
        // empty forever. Same shape as the font setting, which read its value
        // before the settings load and so never applied one.
        ViewModels.PaneViewModel.Trash = maintenance;

        _ = SweepTrashAsync();

        _trashTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _trashTimer.Tick += (_, _) => _ = SweepTrashAsync();
        _trashTimer.Start();
    }

    private async Task SweepTrashAsync()
    {
        if (_trashMaintenance is not { } maintenance) return;

        try
        {
            var policy = AppSettings.Current.Trash;

            var result = await maintenance.SweepAsync(policy, CancellationToken.None);

            // ALWAYS logged, including when it did nothing.
            //
            // It used to speak only when it removed something, so silence meant
            // three different things — the feature is off, it ran and matched
            // nothing, or it never ran at all. For the one feature that deletes
            // files unattended, "I ran and did nothing" is exactly as important
            // as "I removed four", and being unable to tell them apart cost a
            // test round trip.
            var state = !policy.DeleteOldFiles && !policy.LimitSize
                ? "disabled"
                : $"age={(policy.DeleteOldFiles ? $"{policy.DeleteAfterDays}d" : "off")} "
                  + $"size={(policy.LimitSize ? $"{policy.MaximumPercentOfDisk}%" : "off")} "
                  // The field that decides whether it DELETES. Leaving it out
                  // made "removed 0 · OVER LIMIT" ambiguous between "set to warn"
                  // and "set to delete and failing to", which is the whole
                  // question this line exists to answer.
                  + $"when={policy.WhenLimitReached}";

            var freed = ByteSize.Format(result.BytesFreed);

            Console.Error.WriteLine(
                $"[vaktari] trash: {state} · removed {result.Removed} · "
                + $"freed {freed} · skipped {result.Skipped} undated"
                + (result.OverLimit ? " · OVER LIMIT" : ""));

            if (result.Removed > 0)
                _shell.OperationStatus = $"{Naming.BinName}: removed {result.Removed} item(s), freed {freed}";
            else if (result.OverLimit)
                _shell.OperationStatus = $"{Naming.BinTitle} is over its size limit";
        }
        catch (Exception ex)
        {
            // A failed sweep must never take the window with it.
            Console.Error.WriteLine($"[vaktari] trash sweep failed: {ex.Message}");
        }
    }

    // ---- per-pane wiring -----------------------------------------------

    private void WirePane(PaneViewModel pane)
    {
        pane.RenameRequested -= OnRenameRequested;
        pane.RenameRequested += OnRenameRequested;

        pane.PropertyChanged -= OnPaneFilterToggled;
        pane.PropertyChanged += OnPaneFilterToggled;
    }

    /// <summary>Focus now happens through FocusBehavior.FocusOnVisible in the
    /// markup, since there is no field to focus from here.</summary>
    private void OnPaneFilterToggled(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaneViewModel.IsFilterVisible)) return;
        if (sender is not PaneViewModel pane || !pane.IsFilterVisible) return;


    }

    // ---- inline prompt -------------------------------------------------

    private enum PromptMode { None, Rename, ConfirmDelete, ConfirmTrash, ConfirmEmptyTrash, Connect }

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

    private void OnRenameRequested(object? sender, FileEntry entry)
    {
        if (PromptBar is null || PromptInput is null) return;

        _prompt = PromptMode.Rename;
        _renameTarget = entry;

        PromptLabel.Text = "rename to";
        PromptInput.Text = entry.Name;
        PromptInput.IsVisible = true;
        PromptConfirm.Content = "rename";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "enter to confirm · esc to cancel";
        PromptBar.IsVisible = true;

        PromptInput.Focus();

        // **The name, not the extension** — which is what Explorer selects, and
        // what makes "press F2 and type" replace the name rather than turn
        // notes.txt into whatever-was-typed with no extension at all.
        PromptInput.SelectionStart = 0;
        PromptInput.SelectionEnd = Input.RenameSelection.LengthFor(entry.Name, entry.IsDirectory);
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
        var name = PromptInput?.Text ?? "";
        var entry = _renameTarget;

        ClosePrompt();

        switch (mode)
        {
            case PromptMode.ConfirmDelete:
                target?.DeleteSelectedCommand.Execute(null);
                break;

            case PromptMode.ConfirmTrash:
                target?.TrashSelectedCommand.Execute(null);
                break;

            case PromptMode.ConfirmEmptyTrash:
                _ = target?.EmptyTrashAsync();
                break;

            // **Tidied first, as Explorer does.** Windows drops a trailing
            // space or dot at the API level, so a name typed with one asks for
            // something and gets something else — and the file it leaves behind
            // can be awkward for other tools to open or remove. The line below
            // has always trimmed for the same reason; renaming did not.
            case PromptMode.Rename
                when Vaktari.Core.FileSystem.FileNames.Clean(name) is { Length: > 0 } tidy
                     && tidy != entry.Name:
                _ = target?.RenameAsync(entry, tidy);
                break;

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

        PromptLabel.Text = "connect to";

        // From the mounter, not from here: gio takes smb:// and the Windows
        // redirector takes \\server\share, and offering the wrong one is worse
        // than offering nothing.
        PromptInput.Text = _shell.ConnectPrefill;
        PromptInput.IsVisible = true;
        PromptConfirm.Content = "connect";
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

        var count = ViewModels.PaneViewModel.Trash?.List().Count ?? 0;
        if (count == 0) { _shell.ActiveTab.Status = $"{Naming.TheBin} is already empty"; return; }

        _prompt = PromptMode.ConfirmEmptyTrash;

        PromptLabel.Text = $"permanently delete {count:N0} item(s) from {Naming.TheBin}? this cannot be undone";
        PromptInput.IsVisible = false;
        PromptConfirm.Content = $"empty {Naming.BinName}";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "esc to cancel";
        PromptBar.IsVisible = true;

        PromptConfirm.Focus();
    }

    private void AskConfirmDelete()
    {
        if (PromptBar is null) return;
        if (_shell.ActiveTab is not { } pane) return;

        var count = pane.Selection.Count > 0
            ? pane.Selection.Count
            : pane.SelectedEntry is null ? 0 : 1;

        if (count == 0) return;

        _prompt = PromptMode.ConfirmDelete;

        PromptLabel.Text = $"permanently delete {count} item(s)? this cannot be undone";
        PromptInput.IsVisible = false;
        PromptConfirm.Content = "delete permanently";
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

        var count = pane.Selection.Count > 0
            ? pane.Selection.Count
            : pane.SelectedEntry is null ? 0 : 1;

        if (count == 0) return;

        _prompt = PromptMode.ConfirmTrash;

        PromptLabel.Text = $"move {count} item(s) to {Naming.TheBin}?";
        PromptInput.IsVisible = false;
        PromptConfirm.Content = $"move to {Naming.TheBin}";
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

        if (PromptBar is not null) PromptBar.IsVisible = false;
        if (PromptInput is not null) PromptInput.IsVisible = false;
        if (PromptConfirm is not null) PromptConfirm.IsVisible = false;
        if (PromptCancel is not null) PromptCancel.IsVisible = false;
    }

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
        }
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
        if (EntryAt(e.Source) is not { } entry) return;

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
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: FileEntry entry }) return entry;
        }

        return null;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // The normal path, restored. TryOpen drops a duplicate if the fallback
        // in OnTapped has already acted on this same row.
        if (OpensOnSingleClick) return;

        if (EntryAt(e.Source) is { } entry) TryOpen(entry);
    }

    /// <summary>
    /// The narrow set of keys that must be claimed before anything else sees
    /// them. Deliberately tiny: a tunnel handler runs ahead of every control in
    /// the window, so anything added here is taken away from all of them.
    /// </summary>
    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
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

        if (_prompt is PromptMode.Rename) return;

        // Any focused text box owns the keyboard. Checking the type rather
        // than named controls, because the path and filter boxes now live
        // inside a per-pane template and have no generated fields — and it
        // is the more honest rule anyway. Escape and Enter inside those
        // boxes are handled by their own KeyBindings in the markup.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

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
            return;
        }

        if (_shell.ActiveTab is not { } pane) return;

        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                e.Handled = true;
                ShowProperties();
                break;

            case Key.Enter:
                e.Handled = true;
                _ = pane.OpenSelectedAsync();
                break;

            case Key.Back:
                e.Handled = true;
                _ = pane.GoBackAsync();
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
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                if (FocusManager?.GetFocusedElement() is TextBox) break;

                ActiveListing()?.SelectAll();
                e.Handled = true;
                break;

            case Key.Delete when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;

                if (AppSettings.Current.General.ConfirmPermanentDelete)
                    AskConfirmDelete();
                else
                    pane.DeleteSelectedCommand.Execute(null);

                break;

            case Key.Delete:
                e.Handled = true;

                if (AppSettings.Current.General.ConfirmMoveToTrash)
                    AskConfirmTrash();
                else
                    pane.TrashSelectedCommand.Execute(null);

                break;

            // Deliberately duplicated from Window.KeyBindings. This handler is
            // known to run — it is where the crash surfaced — so routing the
            // clipboard through it too means copy cannot fail silently just
            // because a KeyBinding didn't resolve.
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
        }
    }
}
