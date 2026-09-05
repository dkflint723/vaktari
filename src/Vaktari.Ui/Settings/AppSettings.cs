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

    public static void Apply(SettingsState settings)
    {
        // Completed on the way in, by the same rule the settings store applies
        // to a file it read.
        //
        // **The rule used to live here, as a private `Normalise`, and that was
        // one door too few.** It repaired Current and nothing else — so the
        // record a caller handed in stayed exactly as deserialized, and
        // MainWindow's Closed handler, which reads `model.Result` rather than
        // Current, still dereferenced the nulls when that result came from
        // Import. Moved to SettingsRepair in Core, beside the model it repairs,
        // so there is one answer and both doors call it.
        _current = SettingsRepair.Complete(settings);

        // **The one preference a row needs that Core cannot read for itself.**
        // FileKind.DisplayName decides what the name column draws and lives in
        // Vaktari.Core, which does not reference this assembly — so the flag is
        // pushed rather than pulled, the way FileKind.LauncherName is filled in
        // by the platform. Here rather than at the settings dialog's Save,
        // because Apply is the only door into Current: a test, a restore from a
        // copy and the launch-time load all come through it, and any of them
        // leaving the two disagreeing would draw names from the previous
        // settings until the next save.
        Core.FileSystem.FileKind.HideExtensions = _current.Views.HideFileExtensions;

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
                "[vaktari] settings: after completing -> "
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
}
