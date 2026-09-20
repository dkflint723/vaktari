using Vaktari.Core.Sharing;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Finding an interpreter on PATH, and the Windows trap underneath it.
///
/// **A Windows machine with no Python still has python.exe on PATH.** Windows
/// ships App Execution Aliases in %LOCALAPPDATA%\Microsoft\WindowsApps for
/// python, python3 and others: zero-byte reparse points that File.Exists
/// reports as ordinary files, and that open the Microsoft Store when run.
///
/// The sharing provider is constructed with the platform, and its constructor
/// locates copyparty — so treating a stub as an interpreter means running it at
/// startup, and a user who launched a file manager gets a shop instead. That is
/// the same class of unbidden-popup bug as the console window git was flashing,
/// found by asking what happens on a machine without Python rather than by
/// testing on one that has it.
/// </summary>
public class CopypartyBackendTests
{
    /// <summary>Which is protected, so a probe subclass reaches it.</summary>
    private sealed class Probe : CopypartyBackend
    {
        internal static string? Find(string name) => Which(name);

        public override (string? Command, string[] Prefix) Locate() => (null, []);
        public override IReadOnlyList<InstallAttempt> InstallAttempts() => [];
        public override string NotInstalledHint => "";
        public override string NoInstallerHint => "";
        public override string InstalledButNotFoundHint => "";
        public override string InstallFailedHint => "";
    }

    /// <summary>
    /// A temporary directory prepended to PATH for the duration of a test, so
    /// the lookup can be pointed at something known.
    /// </summary>
    private sealed class OnPath : IDisposable
    {
        private readonly string _original;

        public string Directory { get; }

        public OnPath()
        {
            Directory = Path.Combine(
                Path.GetTempPath(), "vaktari-path-" + Guid.NewGuid().ToString("N")[..10]);

            System.IO.Directory.CreateDirectory(Directory);

            _original = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", Directory + Path.PathSeparator + _original);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _original);
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>The name as written on Linux, with PATHEXT applied on Windows.</summary>
    private static string ExecutableName(string stem)
        => OperatingSystem.IsWindows() ? stem + ".exe" : stem;

    [Fact]
    public void An_executable_on_PATH_is_found()
    {
        using var path = new OnPath();

        var file = Path.Combine(path.Directory, ExecutableName("vaktari-probe-tool"));
        File.WriteAllText(file, "not empty");

        Assert.Equal(file, Probe.Find("vaktari-probe-tool"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_name_with_nothing_behind_it_is_not_found()
    {
        using var path = new OnPath();

        Assert.Null(Probe.Find("vaktari-definitely-not-installed"));
    }

    /// <summary>
    /// The real thing, when the machine has one. Skipped rather than faked
    /// because fabricating a zero-byte reparse point needs the symlink
    /// privilege, which is exactly the privilege an ordinary user does not have
    /// — and a test that grants itself one would not be testing the situation
    /// the bug happens in.
    ///
    /// On a machine with a genuine alias this is the whole regression: before
    /// the fix, Which("python3") returned the stub.
    /// </summary>
    [WindowsFact]
    public void An_App_Execution_Alias_is_not_mistaken_for_an_interpreter()
    {
        var aliases = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps");

        var stubs = Directory.Exists(aliases)
            ? Directory.EnumerateFiles(aliases, "*.exe")
                .Where(IsZeroByteReparsePoint)
                .ToList()
            : [];

        // Inconclusive rather than failing: a machine may legitimately have no
        // aliases installed. The assertion below only means anything when it
        // has at least one.
        if (stubs.Count == 0) return;

        foreach (var stub in stubs)
        {
            var name = Path.GetFileNameWithoutExtension(stub);
            var found = Probe.Find(name);

            Assert.False(
                string.Equals(found, stub, StringComparison.OrdinalIgnoreCase),
                $"Which('{name}') returned the App Execution Alias at {stub}; "
                + "running it would open the Microsoft Store");
        }
    }

    private static bool IsZeroByteReparsePoint(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Length == 0 && (file.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// **This default is load-bearing, and it was briefly wrong.**
    ///
    /// It decides whether an install narrates "failed, trying another way"
    /// between attempts. The rule is that the line appears when the next
    /// attempt is another guess, and is suppressed when the next attempt exists
    /// specifically for the failure that just happened — on Linux, PEP 668's
    /// externally-managed-environment, which --break-system-packages answers
    /// directly.
    ///
    /// Moving this code out of Vaktari.Linux inverted it: the hook was named
    /// for "should we continue" and wired to the message, so the line appeared
    /// in exactly the cases the original suppressed it. Nothing caught that,
    /// because the Linux install path has no runtime coverage on a Windows
    /// machine. False here is what keeps a platform with no failure-specific
    /// attempt saying the honest thing.
    /// </summary>
    [Fact]
    public void No_attempt_is_failure_specific_by_default()
    {
        var backend = new Probe();

        Assert.False(backend.NextAttemptAddresses("externally-managed-environment"));
        Assert.False(backend.NextAttemptAddresses("anything at all"));
        Assert.False(backend.NextAttemptAddresses(""));
    }

    /// <summary>
    /// A zero-byte file that is NOT a reparse point is left alone. The rule is
    /// deliberately both conditions: an ordinary symlink to an interpreter is
    /// how several version managers put one on PATH, and rejecting every
    /// reparse point would break them.
    /// </summary>
    [Fact]
    public void A_plain_empty_file_is_still_found()
    {
        using var path = new OnPath();

        var file = Path.Combine(path.Directory, ExecutableName("vaktari-empty-tool"));
        File.WriteAllBytes(file, []);

        Assert.Equal(file, Probe.Find("vaktari-empty-tool"), StringComparer.OrdinalIgnoreCase);
    }
}
