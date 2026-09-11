using System.Text.Json;
using System.Text.Json.Nodes;
using Vaktari.Core.Settings;

namespace Vaktari.Ui.Settings;

/// <summary>
/// Preferences on disk, beside the session but never inside it.
///
/// Same two rules as the session store, for the same reasons: write atomically,
/// because a truncated file reads as amnesia rather than as corruption; and
/// never let a bad file prevent startup.
///
/// One rule dropped, deliberately: no debounce timer. The session changes on
/// every navigation and needs one. Settings change when a person clicks
/// something in a dialog, which is rare enough that a write per change is both
/// affordable and what they expect — closing the dialog and having the file
/// already be right.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly string _tempPath;
    private readonly string _backupPath;
    // A plain object rather than System.Threading.Lock: the newer type would be
    // fine on this target, but nothing here needs it and this cannot be wrong.
    private readonly object _writeLock = new();

    public JsonSettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        _tempPath = _path + ".tmp";
        _backupPath = _path + ".bak";
    }

    /// <summary>
    /// Synchronous, like the session load and for a sharper reason: the startup
    /// setting decides whether the session is read at all, so this has to have
    /// finished before anything else looks at disk.
    ///
    /// Returns defaults rather than null. There is always a valid set of
    /// preferences — an absent file means a first run, not a failure.
    /// </summary>
    public SettingsState Load()
    {
        // A file from a future version is read as defaults rather than
        // partially: silently running with half the settings someone chose is
        // worse than visibly running with none of them. **And it is never
        // written over.** Before this, the next save replaced it — and its
        // backup a save later — with this version's file, so trying an older
        // build once destroyed the choices the newer one had kept. Either
        // file being newer is enough: the backup is overwritten by a save too.
        string? newerBackup = null;

        var state = TryLoad(_path, ours: true, out var newer)
            ?? TryLoad(_backupPath, ours: true, out newerBackup);

        ReadOnlyReason = newer ?? newerBackup;

        return state ?? new SettingsState();
    }

    /// <inheritdoc/>
    public string? ReadOnlyReason { get; private set; }

    /// <summary>
    /// The file as a record, brought up the versions on the way.
    ///
    /// **A file from any other version used to be thrown away.** Only the
    /// current number was kept, so the first release to change it would have
    /// reset every choice on six pages for everyone who upgraded. Now an older
    /// file walks <see cref="SettingsMigrations"/> up to today's format, and
    /// a copy of it as the older version wrote it is kept first, under that
    /// version's name, for the day that version is run again.
    /// </summary>
    /// <param name="ours">Whether this is the store's own file — the one a
    /// newer version's number makes read-only, and the one worth keeping a
    /// copy of — as opposed to a file somebody chose to import.</param>
    /// <param name="newerThanThis">Why the file must not be written, when it
    /// is ours and a newer version wrote it; null otherwise.</param>
    private static SettingsState? TryLoad(string path, bool ours, out string? newerThanThis)
    {
        newerThanThis = null;

        try
        {
            if (!File.Exists(path)) return null;

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject document) return null;

            var version = SettingsMigrations.VersionOf(document);

            if (version > SettingsState.CurrentVersion)
            {
                if (ours)
                    newerThanThis =
                        $"{Path.GetFileName(path)} was written by a newer Vaktari (format {version}; this one "
                        + $"writes format {SettingsState.CurrentVersion}). It is left as it is; changes "
                        + "made here apply until Vaktari closes and are not saved.";

                return null;
            }

            if (version < SettingsState.CurrentVersion)
            {
                if (ours) Keep(path, version);

                if (SettingsMigrations.Upgrade(document, SettingsState.CurrentVersion) is null) return null;
            }

            var state = JsonSerializer.Deserialize(document, SettingsJsonContext.Default.SettingsState);

            // **The one place a file becomes a record, so the one place the
            // groups it never mentioned are put back.** MEASURED 5 September
            // 2026: `{"version":1}` — a valid v1 document naming no section —
            // deserialized into a state whose General, Views, Vcs and the rest
            // were all null, and 1 IS the current version, so the gate below
            // passed it through untouched. Load's own summary promises there is
            // always a valid set of preferences; before this it kept that
            // promise only because AppSettings.Apply happened to repair what it
            // returned, which covered startup and left Import — and therefore
            // the settings dialog's Result — holding the nulls.
            return state is null ? null : SettingsRepair.Complete(state);
        }
        catch
        {
            // Corrupt, truncated, unreadable — all the same answer.
            return null;
        }
    }

    /// <summary>
    /// A copy of the file as the older version wrote it — settings.v1.json —
    /// taken before the walk, and only the first time: later saves are this
    /// version's, and the copy is worth having exactly once.
    /// </summary>
    private static void Keep(string path, int version)
    {
        var kept = Path.Combine(Path.GetDirectoryName(path)!, $"settings.v{version}.json");

        try { if (!File.Exists(kept)) File.Copy(path, kept); }
        catch (Exception ex) { Vaktari.Core.Quiet.Swallowed("settings", ex); }
    }

    public void Save(SettingsState settings)
    {
        if (ReadOnlyReason is { } reason)
        {
            Vaktari.Core.Diagnostics.Log.Warn("settings", "not written: " + reason);
            return;
        }

        lock (_writeLock)
        {
            try
            {
                using (var stream = File.Create(_tempPath))
                {
                    JsonSerializer.Serialize(
                        stream, settings, SettingsJsonContext.Default.SettingsState);
                    stream.Flush();
                }

                if (File.Exists(_path))
                    File.Copy(_path, _backupPath, overwrite: true);

                // Atomic on ext4, btrfs and NTFS: a crash mid-save leaves either
                // the old file or the new one, never a half-written one.
                File.Move(_tempPath, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                // Unlike a lost session write, this one is worth saying out loud —
                // the user just changed a setting and has a right to know it did
                // not stick.
                Console.Error.WriteLine($"[vaktari] settings write failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Writes the defaults out once, on a first run, so the file exists and can
    /// be read or hand-edited before any dialog is built. Never overwrites.
    /// </summary>
    public void EnsureFileExists(SettingsState settings)
    {
        if (!File.Exists(_path)) Save(settings);
    }

    /// <summary>
    /// Where this store keeps its file.
    ///
    /// **Nothing in the application could name it.** The settings dialog's own
    /// footer shows a path on hover, and it is the path of the BINARY — a
    /// different place on every platform, and on none of them this one. So the
    /// file that holds every choice on those six pages could only be found by
    /// knowing where a freedesktop config directory is, or by searching the
    /// disk for it.
    /// </summary>
    public string FilePath => _path;

    /// <summary>
    /// Writes a copy of a state to a path somebody chose.
    ///
    /// Not atomic, unlike <see cref="Save"/>, and deliberately: the temp-then-
    /// move dance protects a file the application will read back on next
    /// launch, and this one it never reads. A half-written export is visible
    /// as a half-written file, which is the honest outcome; a half-written
    /// settings.json reads as amnesia.
    /// </summary>
    public static bool Export(string path, SettingsState settings)
    {
        try
        {
            using var stream = File.Create(path);

            JsonSerializer.Serialize(
                stream, settings, SettingsJsonContext.Default.SettingsState);

            return true;
        }
        catch (Exception ex)
        {
            Vaktari.Core.Quiet.Swallowed("settings-export", ex);
            return false;
        }
    }

    /// <summary>
    /// Reads a copy back, and REFUSES what it cannot read rather than falling
    /// back to defaults.
    ///
    /// The difference from <see cref="Load"/> matters. Load is startup: an
    /// absent or broken file there means a first run or a lost one, and
    /// defaults are the only thing that can happen next. This is a person
    /// pointing at a file they believe holds their settings — answering it with
    /// defaults would silently RESET every choice they have, which is the exact
    /// opposite of what they asked for. Null here becomes a refusal on screen.
    /// </summary>
    public static SettingsState? Import(string path)
    {
        // Not the store's own file: a newer number is a refusal here rather
        // than a read-only run, and no copy of it is kept — it is theirs.
        return TryLoad(path, ours: false, out _);
    }
}
