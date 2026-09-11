using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What the share feature asks pip and pipx to install.
///
/// **It asked for "copyparty", which is whatever PyPI holds this morning.**
/// Neither tool takes a hash on the command line, so a pinned version is as
/// far as provenance reaches by this route — but it is the difference between
/// installing a program somebody has run and installing the newest thing
/// published under the name, at whatever moment the user clicked.
///
/// A stub pipx and a stub py are planted on PATH so the attempts exist on a
/// machine with neither — without that the list is empty and the assertion
/// proves nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CopypartyPinTests : IDisposable
{
    private readonly string _bin =
        Path.Combine(Path.GetTempPath(), "vaktari-pin-" + Guid.NewGuid().ToString("N")[..10]);

    private readonly string _path = Environment.GetEnvironmentVariable("PATH") ?? "";

    public CopypartyPinTests()
    {
        Directory.CreateDirectory(_bin);
        File.WriteAllText(Path.Combine(_bin, "pipx.exe"), "not empty");
        File.WriteAllText(Path.Combine(_bin, "py.exe"), "not empty");
        Environment.SetEnvironmentVariable("PATH", _bin + Path.PathSeparator + _path);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        try { Directory.Delete(_bin, recursive: true); } catch { /* temp */ }
    }

    [WindowsFact]
    public void Every_install_attempt_asks_for_one_version_of_copyparty()
    {
        var attempts = new WindowsCopyparty().InstallAttempts();

        Assert.True(attempts.Count >= 2, "the stubs on PATH were not found; the assertion below would be vacuous");

        foreach (var attempt in attempts)
            Assert.Equal("copyparty==" + WindowsCopyparty.CopypartyVersion, attempt.Args[^1]);
    }

    [Fact]
    public void The_pinned_version_is_a_version()
        => Assert.Matches(@"^\d+\.\d+\.\d+$", WindowsCopyparty.CopypartyVersion);
}
