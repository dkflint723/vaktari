using Microsoft.Win32;
using Vaktari.Core;

namespace Vaktari.Windows;

/// <summary>
/// Makes Vaktari the program a double-clicked folder opens, by redirecting the
/// default verb on the shell's Directory and Drive classes.
///
/// **Windows has no "default file manager" setting, and this is not one.**
/// Nothing in Settings offers to replace Explorer, and Explorer is not
/// replaceable. What the shell does allow is choosing which verb is the DEFAULT
/// for a class — the one a double-click runs — and pointing that verb at
/// another program. Every third-party file manager on Windows does exactly
/// this; the shape here was read off a working installation of one rather than
/// guessed, including the Icon value and the quoting of "%1".
///
/// **Per-user, under HKCU.** No administrator, no UAC prompt, and nothing that
/// touches another account. HKLM would need elevation and would impose the
/// choice on everyone who signs in.
///
/// **What it does not cover**, and the UI says so: Win+E, the taskbar's File
/// Explorer pin, "Show in folder" from other applications, and Explorer's own
/// address bar all go to Explorer regardless. Those are wired to Explorer
/// directly rather than through the class default.
/// </summary>
internal sealed class WindowsDefaultFileManager : IDefaultFileManager
{
    /// <summary>
    /// The verb key we own. Named rather than reusing "open": the shell's own
    /// `open` verb on Directory carries a DelegateExecute that hands the call
    /// to Explorer's COM handler, and overwriting it is how folder navigation
    /// inside Explorer breaks. A verb of our own, made the default, leaves that
    /// machinery untouched.
    /// </summary>
    private const string Verb = "OpenInVaktari";

    /// <summary>
    /// Both classes, because they are separate: Directory is a folder,
    /// Drive is what you double-click in "This PC". Setting only the first
    /// leaves drives opening in Explorer, which reads as the feature half
    /// working rather than as a considered boundary.
    /// </summary>
    private static readonly string[] Classes = ["Directory", "Drive"];

    /// <summary>
    /// Where the displaced handler is stashed. Under our own key, not theirs:
    /// this machine had OneCommander registered as the default, and a feature
    /// that overwrote that without being able to give it back would be taking
    /// something it cannot return.
    /// </summary>
    private const string BackupRoot = @"Software\Vaktari\DefaultFileManager";

    /// <summary>
    /// Scoped by the same root the classes are, so a test pointed at a scratch
    /// subtree cannot reach the real record.
    ///
    /// **It was a constant, and that was a live defect.** The classes path was
    /// injectable and this was not, so the suite's cleanup deleted the
    /// production backup on any machine where it ran — including the one where
    /// the feature was actually in use. The failure is silent and only shows up
    /// when someone presses "stop being the default" and gets an unclaimed
    /// class instead of the handler they had before.
    /// </summary>
    private string BackupKey => _root == DefaultRoot
        ? BackupRoot
        : $@"{BackupRoot}\scoped_{_root.Replace('\\', '_')}";

    internal const string DefaultRoot = @"Software\Classes";

    private readonly string _root;
    private readonly string _exe;

    /// <summary>
    /// The registry root is injected so tests can run against a scratch subtree
    /// instead of the live shell classes. Verifying this by writing to the real
    /// Directory\shell would change the machine's actual behaviour as a side
    /// effect of running the suite.
    /// </summary>
    internal WindowsDefaultFileManager(string? exePath = null, string root = DefaultRoot)
    {
        _root = root;
        _exe = exePath ?? Environment.ProcessPath ?? "";
    }

    public string Caveat =>
        "Windows has no setting for this, so Vaktari registers itself as the "
        + "handler for folders and drives — double-clicking a folder will open "
        + "Vaktari. Win+E, the File Explorer button on the taskbar, and "
        + "\"show in folder\" from other programs still open Explorer: those go "
        + "to it directly rather than asking Windows what a folder should open in.";

    /// <summary>
    /// The verb a previous name of this application registered.
    ///
    /// Upgrading from Heimdall deletes that installation and leaves its verb
    /// behind pointing at a binary that no longer exists — after which every
    /// double-clicked folder fails with an error naming a missing file. This is
    /// the same wound the uninstaller now avoids, arriving by a different route,
    /// and it was found by auditing rather than by anyone hitting it twice.
    /// </summary>
    private const string PreviousVerb = "OpenInHeimdall";

    /// <summary>
    /// Removes a dead registration left by a previous name, and hands the class
    /// back to whoever held it before that.
    ///
    /// **Only when the command genuinely points at something missing.** A
    /// Heimdall that is still installed and still works is not ours to
    /// dispossess — somebody may be running both.
    ///
    /// Quiet and best-effort: this runs at startup, and a registry it cannot
    /// read is not a reason to fail to launch.
    /// </summary>
    public void HealPreviousName()
    {
        foreach (var cls in Classes)
        {
            try
            {
                using var shell = Registry.CurrentUser.OpenSubKey(
                    $@"{_root}\{cls}\shell", writable: true);

                if (shell is null) continue;
                if (shell.GetValue(null) as string != PreviousVerb) continue;

                using (var command = shell.OpenSubKey($@"{PreviousVerb}\command"))
                {
                    var line = command?.GetValue(null) as string ?? "";
                    if (line.Length > 0 && TargetExists(line)) continue;
                }

                // The old name's own record of who held it first. Falling back
                // to clearing the value leaves the class unclaimed, which is
                // what Windows looked like before anybody registered.
                var previous = PreviousOwner(cls);

                if (previous is { Length: > 0 }) shell.SetValue(null, previous);
                else shell.DeleteValue("", throwOnMissingValue: false);

                shell.DeleteSubKeyTree(PreviousVerb, throwOnMissingSubKey: false);

                Console.Error.WriteLine(
                    $"[vaktari] cleared a dead {PreviousVerb} registration on {cls}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vaktari] could not heal {cls}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The command a folder or a drive is opened with.
    ///
    /// A folder's "%1" is quoted: a path with a space in it is the common case,
    /// not the edge case. **A drive's is not.** Its "%1" is a root, C:\, and
    /// quoted it ends the line in \" — which the command-line rules every
    /// program follows read as an escaped quote, so the argument arrived as
    /// C:" and double-clicking a drive opened nothing. A drive root has no
    /// space in it to protect.
    /// </summary>
    internal static string CommandFor(string exe, string cls)
        => cls == "Drive" ? $"\"{exe}\" %1" : $"\"{exe}\" \"%1\"";

    /// <summary>The executable out of a `"path" "%1"` command line.</summary>
    private static bool TargetExists(string command)
    {
        var exe = command.StartsWith('"')
            ? command[1..].Split('"').FirstOrDefault() ?? ""
            : command.Split(' ').FirstOrDefault() ?? "";

        return exe.Length > 0 && File.Exists(exe);
    }

    private string? PreviousOwner(string cls)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Heimdall\DefaultFileManager");

        return key?.GetValue(cls) as string;
    }

    public bool IsDefault()
    {
        foreach (var cls in Classes)
        {
            using var shell = Registry.CurrentUser.OpenSubKey($@"{_root}\{cls}\shell");
            if (shell?.GetValue(null) as string != Verb) return false;
        }

        return true;
    }

    public DefaultChange MakeDefault()
    {
        if (string.IsNullOrEmpty(_exe))
            return new DefaultChange(false, "Vaktari could not work out its own path.");

        try
        {
            foreach (var cls in Classes)
            {
                using var shell = Registry.CurrentUser.CreateSubKey($@"{_root}\{cls}\shell")
                    ?? throw new InvalidOperationException($@"cannot open {cls}\shell");

                // Read BEFORE writing, and only stash the first time: pressing
                // the button twice must not record "Vaktari" as the thing to
                // restore to.
                var previous = shell.GetValue(null) as string ?? "";
                if (previous != Verb) Remember(cls, previous);

                using var verb = shell.CreateSubKey(Verb)
                    ?? throw new InvalidOperationException("cannot create the verb key");

                verb.SetValue(null, "Open in Vaktari");
                verb.SetValue("Icon", _exe);

                using var command = verb.CreateSubKey("command")
                    ?? throw new InvalidOperationException("cannot create the command key");

                command.SetValue(null, CommandFor(_exe, cls));

                shell.SetValue(null, Verb);
            }

            return new DefaultChange(true, "Vaktari now opens folders and drives.");
        }
        catch (Exception ex)
        {
            return new DefaultChange(false, $"Windows refused the change: {ex.Message}");
        }
    }

    public DefaultChange Restore()
    {
        try
        {
            foreach (var cls in Classes)
            {
                using var shell = Registry.CurrentUser.OpenSubKey($@"{_root}\{cls}\shell", writable: true);
                if (shell is null) continue;

                var previous = Recall(cls);

                // An empty remembered value is meaningful: it is what the shell
                // looked like before anyone claimed the class, and writing ""
                // back is what restores that. Deleting the value instead would
                // be a different state.
                if (previous is null) shell.DeleteValue("", throwOnMissingValue: false);
                else shell.SetValue(null, previous);

                shell.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
            }

            Forget();
            return new DefaultChange(true, "Folders open in whatever opened them before.");
        }
        catch (Exception ex)
        {
            return new DefaultChange(false, $"Windows refused the change: {ex.Message}");
        }
    }

    private void Remember(string cls, string previous)
    {
        using var key = Registry.CurrentUser.CreateSubKey(BackupKey);
        key?.SetValue(cls, previous);
    }

    private string? Recall(string cls)
    {
        using var key = Registry.CurrentUser.OpenSubKey(BackupKey);
        return key?.GetValue(cls) as string;
    }

    private void Forget() =>
        Registry.CurrentUser.DeleteSubKeyTree(BackupKey, throwOnMissingSubKey: false);
}
