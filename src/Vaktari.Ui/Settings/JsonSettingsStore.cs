using System.Text.Json;
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
        var state = TryLoad(_path) ?? TryLoad(_backupPath);

        // A file from a future version is ignored rather than partially read.
        // Silently running with half the settings someone chose is worse than
        // visibly running with none of them.
        return state?.Version == SettingsState.CurrentVersion ? state : new SettingsState();
    }

    private static SettingsState? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.SettingsState);
        }
        catch
        {
            // Corrupt, truncated, unreadable — all the same answer.
            return null;
        }
    }

    public void Save(SettingsState settings)
    {
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
        var state = TryLoad(path);

        // Same version rule as Load, for the same reason: half a file from
        // another version is worse than none of it.
        return state?.Version == SettingsState.CurrentVersion ? state : null;
    }
}
