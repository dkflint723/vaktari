using System.Diagnostics;
using Vaktari.Core;
using Vaktari.Core.Sharing;

namespace Vaktari.Windows;

/// <summary>
/// Where copyparty lives on Windows and how to install it there.
///
/// The Linux note said this was reachable — "copyparty runs on Windows wherever
/// Python does" — and it is, with one addition that matters more here than
/// there: copyparty publishes a standalone `copyparty.exe` with Python already
/// inside it. On a machine with no Python at all that is the only thing that
/// will ever work, so it is looked for by name and named in the hint.
/// </summary>
public sealed class WindowsCopyparty : CopypartyBackend
{
    /// <summary>
    /// The copyparty this version of Vaktari installs. **A version, not
    /// "latest":** pip and pipx take no hash on the command line, so a
    /// pinned version is as far as provenance reaches by this route — but
    /// it is the difference between installing a program somebody has run
    /// and whatever PyPI holds this morning. Moved on purpose, together
    /// with a look at its changelog.
    /// </summary>
    internal const string CopypartyVersion = "1.20.23";

    /// <summary>
    /// A real executable first, then the module, then a downloaded sfx or exe.
    ///
    /// `py` before `python` deliberately: the Python launcher is what a Windows
    /// install puts on PATH for everyone, whereas a bare `python` may be the
    /// App Execution Alias stub that opens the Microsoft Store instead of
    /// running anything.
    /// </summary>
    public override (string? Command, string[] Prefix) Locate()
    {
        if (Which("copyparty") is { } binary) return (binary, []);

        foreach (var launcher in new[] { "py", "python3", "python" })
        {
            if (Which(launcher) is not { } python) continue;

            // Only claim the module if it is actually importable, or every
            // share would fail at launch with an unhelpful message.
            if (Run(python, ["-c", "import copyparty"]) == 0)
                return (python, ["-m", "copyparty"]);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");

        // The standalone build needs no Python at all; the sfx still does, so
        // it is only worth naming when something can run it.
        foreach (var candidate in new[]
                 {
                     Path.Combine(downloads, "copyparty.exe"),
                     Path.Combine(profile, "copyparty.exe"),
                 })
        {
            if (File.Exists(candidate)) return (candidate, []);
        }

        // The sfx is a Python script, so it is only worth naming when there is
        // something on the machine that can run it.
        var runner = Which("py") ?? Which("python3") ?? Which("python");

        if (runner is not null)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(downloads, "copyparty-sfx.py"),
                         Path.Combine(profile, "copyparty-sfx.py"),
                     })
            {
                if (File.Exists(candidate)) return (runner, [candidate]);
            }
        }

        return (null, []);
    }

    /// <summary>
    /// pipx first for the same reason as on Linux — it isolates the package and
    /// leaves the system Python alone — then a plain user install.
    ///
    /// No --break-system-packages third attempt: PEP 668's externally-managed
    /// marker is a distro packaging decision, and python.org and Store installs
    /// of Python on Windows do not set it.
    /// </summary>
    public override IReadOnlyList<InstallAttempt> InstallAttempts()
    {
        var attempts = new List<InstallAttempt>();

        if (Which("pipx") is { } pipx)
            attempts.Add(new(pipx,
                ["install", "copyparty==" + CopypartyVersion],
                "pipx install copyparty==" + CopypartyVersion));

        foreach (var launcher in new[] { "py", "python3", "python" })
        {
            if (Which(launcher) is not { } python) continue;

            attempts.Add(new(python,
                ["-m", "pip", "install", "--user", "--upgrade", "copyparty==" + CopypartyVersion],
                $"{launcher} -m pip install --user copyparty=={CopypartyVersion}"));

            // One launcher is enough; trying all three would run the same
            // install three times against whichever Python answered first.
            break;
        }

        return attempts;
    }

    /// <summary>What netsh says about the current firewall profile, for
    /// tests. Null runs netsh.</summary>
    internal static Func<string?>? ProfileOverride { get; set; }

    /// <summary>
    /// **A share on a network Windows marks Public — a café, a hotel, an
    /// airport — is a folder offered to everyone on it.** The password stands
    /// between them and the files, and it is a good one; but the person
    /// sharing chose "this folder", not "this folder, to this whole room",
    /// and the difference is worth a line. Windows already knows which kind
    /// of network it is on: the active firewall profile says, and netsh
    /// prints it — in the language of the installation, so on a Windows in
    /// another language nothing is recognised and nothing is said, which is
    /// the quiet side to fail on.
    /// </summary>
    public override string? StartWarning()
        => (ProfileOverride ?? CurrentProfile)() is { } profile
           && profile.Contains("Public Profile", StringComparison.OrdinalIgnoreCase)
            ? "this machine is on a network Windows marks as public — anyone on it with the address can try the password"
            : null;

    private static string? CurrentProfile()
    {
        try
        {
            var info = new ProcessStartInfo("netsh")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in new[] { "advfirewall", "show", "currentprofile" }) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);

            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();

            return process.WaitForExit(5000) ? output : null;
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("sharing", ex);
            return null;
        }
    }

    public override string NotInstalledHint =>
        "copyparty is not installed — install Python and run "
        + "'py -m pip install --user copyparty', or put copyparty.exe in your Downloads folder";

    public override string NoInstallerHint =>
        "no Python found — install it from python.org, or download copyparty.exe "
        + "to your Downloads folder";

    /// <summary>
    /// The Windows shape of the same failure Linux describes: pip's --user
    /// scripts directory is under %APPDATA%\Python and is not on PATH by
    /// default. The module form still works, which is what Locate tries next.
    /// </summary>
    public override string InstalledButNotFoundHint =>
        "installed, but not runnable yet — restart Vaktari, or check that "
        + @"%APPDATA%\Python\Scripts is on PATH";

    public override string InstallFailedHint =>
        "install failed — try 'py -m pip install --user copyparty' in a terminal";
}
