using Vaktari.Core.Settings;

namespace Vaktari.Ui.Settings;

/// <summary>
/// The live preferences, reachable from anywhere that needs them.
///
/// A static rather than constructor injection, deliberately and to match what
/// this codebase already does: <c>IconLoader.Provider</c>,
/// <c>ThumbnailLoader.Provider</c>, <c>RowMetadata.Provider</c> and
/// <c>RowTags.Store</c> are all statics for the same reason. Settings are read
/// by attached properties on realized rows, which have no constructor to inject
/// into — threading a settings object down to them would mean widening
/// <c>FileEntry</c> or passing it through every template, and the first of those
/// is explicitly forbidden.
///
/// <see cref="Changed"/> exists because some settings must take effect the
/// moment they are saved rather than at next launch. Anything that reads
/// <see cref="Current"/> more than once should subscribe.
/// </summary>
public static class AppSettings
{
    private static SettingsState _current = new();

    /// <summary>Never null. An absent or unreadable file yields defaults.</summary>
    public static SettingsState Current => _current;

    /// <summary>Raised after <see cref="Current"/> has already been swapped, so
    /// a handler reading it sees the new values rather than the old.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// How <see cref="Current"/> reaches the disk. Set once at startup by the
    /// window that owns the store.
    ///
    /// The settings dialog saves through its own Closed handler, which is fine
    /// for a dialog — but a preference toggled from the listing menu has no
    /// dialog to close, and a column choice that forgets itself on restart is
    /// not a preference.
    /// </summary>
    public static Action<SettingsState>? Persist { get; set; }

    /// <summary>
    /// Change one thing, apply it, and write it down. The whole state is
    /// rebuilt around the change because the graph is immutable records.
    /// </summary>
    public static void Update(Func<SettingsState, SettingsState> change)
    {
        var next = change(Current);

        Apply(next);
        Persist?.Invoke(next);
    }

    public static void Apply(SettingsState settings)
    {
        _current = Normalise(settings);

        // **PROVEN 30 July 2026: deserialization does NOT run property
        // initializers here.** A key absent from settings.json arrives as
        // `default(T)`, not as the declared default. The control below printed
        // `False` from the file and `True` from a freshly constructed record in
        // the same breath. `Vcs` arriving null was the same mechanism.
        //
        // Kept as an instrument because every `= true` default in SettingsModel is
        // therefore decorative for any file written before that property existed,
        // and the next one to bite will be found here.
        if (Environment.GetEnvironmentVariable("VAKTARI_SETTINGS_DEBUG") == "1")
        {
            var raw = ReferenceEquals(settings.Views, null)
                ? "views=NULL"
                : $"views.narrowPanel={settings.Views.NarrowDetailsPanel} "
                  + $"views.keepWidth={settings.Views.KeepWidthAfterPanelClose}";

            Console.Error.WriteLine(
                $"[vaktari] settings: as deserialized -> {raw}"
                + $" · vcs={(ReferenceEquals(settings.Vcs, null) ? "NULL" : "present")}");

            Console.Error.WriteLine(
                "[vaktari] settings: after normalise -> "
                + $"views.narrowPanel={_current.Views.NarrowDetailsPanel} "
                + $"views.keepWidth={_current.Views.KeepWidthAfterPanelClose} "
                + $"vcs.show={_current.Vcs.ShowDecorations}");

            // What a FRESH record claims, as the control. If this says True and
            // the deserialized one says False, the initializer is being skipped.
            var fresh = new ViewSettings();
            Console.Error.WriteLine(
                $"[vaktari] settings: a fresh ViewSettings claims "
                + $"keepWidth={fresh.KeepWidthAfterPanelClose}");
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Guarantees every group is present, because the summary above promises it
    /// and deserialization does not deliver it.
    ///
    /// **Observed, not theoretical:** a `settings.json` written before
    /// `VcsSettings` existed produced `Current.Vcs == null` despite
    /// `SettingsState` declaring `Vcs { get; init; } = new()`. That crashed the
    /// listing, then the settings dialog, and would have crashed the save. Every
    /// group is equally exposed the next time one is added, so this is fixed at
    /// the boundary rather than at each of the dozens of read sites.
    ///
    /// **`ReferenceEquals(x, null)` rather than `x is null` or `x ?? new()`:**
    /// these properties are non-nullable reference types, so the nullable
    /// analyser may call the comparison redundant — and this project builds with
    /// warnings as errors. A method call cannot be warned about.
    /// </summary>
    private static SettingsState Normalise(SettingsState settings) => settings with
    {
        General = NormaliseGeneral(settings.General),
        Startup = ReferenceEquals(settings.Startup, null) ? new() : settings.Startup,
        Views = NormaliseViews(settings.Views),
        Vcs = ReferenceEquals(settings.Vcs, null) ? new() : settings.Vcs,
        Navigation = ReferenceEquals(settings.Navigation, null) ? new() : settings.Navigation,
        ContextMenu = ReferenceEquals(settings.ContextMenu, null) ? new() : settings.ContextMenu,
        Trash = ReferenceEquals(settings.Trash, null) ? new() : settings.Trash,
    };

    /// <summary>
    /// The same hazard one level down, on STRINGS rather than groups.
    ///
    /// **A missing key arrives as null, not as the declared default**, and the
    /// note above says so — but the guard only ever covered whole groups. A
    /// string property added in a later version is null for every settings file
    /// written before it existed, which is every upgrade, and 0.8.0 shipped a
    /// NullReferenceException out of the MainWindow constructor because of
    /// exactly that: the application would not start.
    ///
    /// Coerced here rather than at each use, because "each use" is every future
    /// caller of a property somebody adds next year.
    /// </summary>
    private static GeneralSettings NormaliseGeneral(GeneralSettings general)
    {
        if (ReferenceEquals(general, null)) return new GeneralSettings();

        return general with
        {
            IconThemeFolder = general.IconThemeFolder ?? "",
            PreferredTerminal = general.PreferredTerminal ?? "",
        };
    }

    /// <summary>
    /// `ViewSettings` nests three groups of its own, and they are exposed to
    /// exactly the same problem. Normalising the outer one and stopping there
    /// would look handled while `views.Icons.Spacing` still threw.
    /// </summary>
    private static ViewSettings NormaliseViews(ViewSettings views)
    {
        if (ReferenceEquals(views, null)) return new ViewSettings();

        return views with
        {
            Icons = ReferenceEquals(views.Icons, null) ? new() : views.Icons,
            Compact = ReferenceEquals(views.Compact, null) ? new() : views.Compact,
            Details = ReferenceEquals(views.Details, null) ? new() : views.Details,
        };
    }
}
