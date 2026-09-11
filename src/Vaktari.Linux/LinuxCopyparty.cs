using Vaktari.Core.Sharing;

namespace Vaktari.Linux;

/// <summary>
/// Where copyparty lives on Linux and how to install it there.
///
/// All that is left of what used to be a 476-line CopypartyShare in this
/// assembly: writing the config, launching, tracking and stopping moved to
/// Core when Windows wanted the same behaviour. This is the part that was
/// genuinely about Linux.
/// </summary>
public sealed class LinuxCopyparty : CopypartyBackend
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
    /// Found rather than bundled, and in the order a user would expect: a real
    /// executable first, then the module, then a downloaded sfx.
    /// </summary>
    public override (string? Command, string[] Prefix) Locate()
    {
        if (Which("copyparty") is { } binary) return (binary, []);

        if (Which("python3") is { } python)
        {
            // Only claim the module if it is actually importable, or every
            // share would fail at launch with an unhelpful message.
            if (Run(python, ["-c", "import copyparty"]) == 0)
                return (python, ["-m", "copyparty"]);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            foreach (var candidate in new[]
                     {
                         Path.Combine(home, "copyparty-sfx.py"),
                         Path.Combine(home, ".local", "bin", "copyparty-sfx.py"),
                         Path.Combine(home, "Downloads", "copyparty-sfx.py"),
                     })
            {
                if (File.Exists(candidate)) return (python, [candidate]);
            }
        }

        return (null, []);
    }

    /// <summary>
    /// pipx first: it isolates the package in its own environment and leaves
    /// the system Python alone. Then a plain user install. Only if that is
    /// refused because the distro marks its Python externally managed (PEP 668
    /// — Fedora does) do we pass --break-system-packages, and a --user install
    /// cannot damage system packages even then.
    /// </summary>
    public override IReadOnlyList<InstallAttempt> InstallAttempts()
    {
        var attempts = new List<InstallAttempt>();

        if (Which("pipx") is { } pipx)
            attempts.Add(new(pipx,
                ["install", "copyparty==" + CopypartyVersion],
                "pipx install copyparty==" + CopypartyVersion));

        if (Which("python3") is { } python)
        {
            attempts.Add(new(python,
                ["-m", "pip", "install", "--user", "--upgrade", "copyparty==" + CopypartyVersion],
                "pip install --user copyparty==" + CopypartyVersion));

            attempts.Add(new(python,
                ["-m", "pip", "install", "--user", "--upgrade", "--break-system-packages", "copyparty==" + CopypartyVersion],
                "pip install --user --break-system-packages copyparty==" + CopypartyVersion));
        }

        return attempts;
    }

    /// <summary>
    /// The --break-system-packages attempt exists for exactly one failure: PEP
    /// 668's externally-managed-environment, which Fedora and Debian both set.
    /// When that is what happened, the next attempt is a targeted answer rather
    /// than another guess, and saying "trying another way" would undersell it.
    /// </summary>
    public override bool NextAttemptAddresses(string output)
        => output.Contains("externally-managed-environment", StringComparison.OrdinalIgnoreCase);

    public override string NotInstalledHint =>
        "copyparty is not installed — try 'python3 -m pip install --user copyparty'";

    public override string NoInstallerHint => "no python3 or pipx found — cannot install";

    public override string InstalledButNotFoundHint =>
        "installed, but not runnable yet — check that ~/.local/bin is on PATH";

    public override string InstallFailedHint =>
        "install failed — try 'python3 -m pip install --user copyparty' in a terminal";
}
