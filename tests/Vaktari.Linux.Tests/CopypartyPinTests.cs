using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// What the share feature asks pip and pipx to install — the Linux half of
/// the promise the Windows tests of the same name pin.
///
/// It asked for "copyparty", which is whatever PyPI holds this morning. A
/// pinned version is as far as provenance reaches by this route, and it is the
/// difference between a program somebody has run and the newest thing under
/// the name at the moment of the click.
///
/// A stub pipx and python3 are planted on PATH so the attempts exist on a
/// machine with neither.
/// </summary>
public sealed class CopypartyPinTests : IDisposable
{
    private readonly string _bin =
        Path.Combine(Path.GetTempPath(), "vaktari-pin-" + Guid.NewGuid().ToString("N")[..10]);

    private readonly string _path = Environment.GetEnvironmentVariable("PATH") ?? "";

    public CopypartyPinTests()
    {
        Directory.CreateDirectory(_bin);
        File.WriteAllText(Path.Combine(_bin, Executable("pipx")), "not empty");
        File.WriteAllText(Path.Combine(_bin, Executable("python3")), "not empty");
        Environment.SetEnvironmentVariable("PATH", _bin + Path.PathSeparator + _path);
    }

    /// <summary>This project is built and run on Windows too, where the lookup
    /// applies PATHEXT and a bare name is invisible to it.</summary>
    private static string Executable(string stem)
        => OperatingSystem.IsWindows() ? stem + ".exe" : stem;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        try { Directory.Delete(_bin, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void Every_install_attempt_asks_for_one_version_of_copyparty()
    {
        var attempts = new LinuxCopyparty().InstallAttempts();

        Assert.True(attempts.Count >= 3, "the stubs on PATH were not found; the assertion below would be vacuous");

        foreach (var attempt in attempts)
            Assert.Equal("copyparty==" + LinuxCopyparty.CopypartyVersion, attempt.Args[^1]);
    }

    [Fact]
    public void The_pinned_version_is_a_version()
        => Assert.Matches(@"^\d+\.\d+\.\d+$", LinuxCopyparty.CopypartyVersion);
}
