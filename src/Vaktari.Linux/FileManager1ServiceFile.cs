namespace Vaktari.Linux;

/// <summary>
/// The D-Bus service file, which is what lets a CLOSED Vaktari be reached.
///
/// Without it the bus name is answered only while a window happens to be open,
/// so "show in folder" works or does nothing depending on what the user last
/// closed — and a file manager that answers half the time is worse than one
/// that never does, because there is nothing to diagnose. With it, dbus-daemon
/// starts Vaktari on demand and holds the pending call until the name is
/// acquired.
///
/// **Under $XDG_DATA_HOME, never /usr/share.** Nautilus and Dolphin each ship
/// /usr/share/dbus-1/services/org.freedesktop.FileManager1.service — the same
/// path, byte for byte — so a package of ours that shipped one could not be
/// installed beside either of them. The per-user directory is searched FIRST by
/// the specification, which is also the right precedence: this file exists
/// because the user asked for Vaktari to open folders, and that outranks
/// whatever their distribution installed.
///
/// **Written and removed by ReconcileAsync, from the same boolean that decides
/// the bus name, and never by the installer.** That is the whole answer to the
/// stale-Exec wound that WindowsDefaultFileManager.HealPreviousName exists to
/// treat: reconcile runs on every launch, so a file naming a binary that has
/// moved is rewritten the first time Vaktari runs from its new home, and a file
/// left behind after the user chose another file manager is deleted the first
/// time Vaktari notices. No separate healing pass, and nothing to keep in step.
/// </summary>
internal static class FileManager1ServiceFile
{
    internal const string FileName = "org.freedesktop.FileManager1.service";

    /// <summary>
    /// The data directory the tests use instead of the session's.
    ///
    /// **A seam rather than the tests moving XDG_DATA_HOME**, which is process
    /// -global: xUnit runs test classes in parallel, and redirecting it here
    /// took the terminal-entry tests' data directory out from under them — a
    /// failure that appeared only on the Linux job, because that is the only
    /// one where those tests have anything to find.
    ///
    /// The data HOME rather than the whole directory, so the dbus-1/services
    /// layout below stays the thing under test.
    /// </summary>
    internal static string? DataHomeOverride { get; set; }

    internal static string Directory
    {
        get
        {

            // The same fallback DesktopEntries, SharedMimeInfo, XdgTrash and
            // LinuxScriptRunner each spell out; spelled again rather than
            // shared, because those four each want a different leaf and this
            // wants none of them.
            var dataHome = DataHomeOverride
                           ?? Environment.GetEnvironmentVariable("XDG_DATA_HOME");

            if (string.IsNullOrWhiteSpace(dataHome))
                dataHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share");

            return Path.Combine(dataHome, "dbus-1", "services");
        }
    }

    internal static string FilePath => Path.Combine(Directory, FileName);

    /// <summary>
    /// **Exec must be an absolute path.** dbus-daemon does not consult PATH; a
    /// bare "vaktari" parses, installs, and then fails to activate — which from
    /// outside is indistinguishable from the name never having been claimed.
    /// Environment.ProcessPath is the same answer the startup line prints, and
    /// it resolves the ~/.local/bin/vaktari symlink to the real binary, which is
    /// what has to be executed anyway.
    /// </summary>
    internal static string Text(string exec)
        => "[D-BUS Service]\n"
         + $"Name={FreedesktopFileManager.BusName}\n"
         + $"Exec={exec}\n";

    /// <summary>
    /// Writes it if it is missing or names a different binary, and otherwise
    /// touches nothing.
    ///
    /// Compared before writing rather than rewritten every launch: this runs on
    /// every start, and churning a file's mtime for no change is how a backup
    /// tool or a file watcher ends up with something to say every time the
    /// application opens.
    /// </summary>
    internal static void Install(string? exec)
    {
        if (string.IsNullOrEmpty(exec)) return;

        try
        {
            var wanted = Text(exec);

            if (File.Exists(FilePath) && File.ReadAllText(FilePath) == wanted) return;

            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, wanted);
        }
        catch (Exception ex)
        {
            // Said out loud rather than swallowed. Without the file Vaktari
            // still answers while it is running, so this is a degradation and
            // not a failure — but a silent one would leave "works sometimes"
            // with no explanation anywhere.
            Console.Error.WriteLine($"[vaktari] could not write {FilePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Takes it away again. Called when Vaktari is not the desktop's file
    /// manager, so a user who changed their mind is not left with a bus that
    /// starts Vaktari for a role it will decline the moment it is up.
    /// </summary>
    internal static void Remove()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] could not remove {FilePath}: {ex.Message}");
        }
    }

    /// <summary>The Exec line, for the tests and for nothing else.</summary>
    internal static string? ExecIn(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith("Exec=", StringComparison.Ordinal))
                return line[5..].Trim();
        }

        return null;
    }
}
