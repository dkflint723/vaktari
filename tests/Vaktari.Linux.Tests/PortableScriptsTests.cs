using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// **A portable copy made its scripts folder on every machine it ran on**, in
/// ~/.local/share/vaktari/scripts, and went looking there for the old names'
/// folders to move — a write outside the one folder it promises to stay in,
/// and none of the scripts it carried. The Windows runner already lived in the
/// state directory, which is the portable folder when there is one.
/// </summary>
public sealed class PortableScriptsTests : IDisposable
{
    private readonly string _portable = Directory.CreateTempSubdirectory("vaktari-portable-scripts").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_portable, recursive: true); }
        catch (Exception) { /* a temp folder left behind is not worth failing over */ }
    }

    [Fact]
    public void A_portable_copy_keeps_its_scripts_in_its_own_folder()
    {
        var runner = new LinuxScriptRunner(_portable);

        Assert.Equal(Path.Combine(_portable, "scripts"), runner.ScriptsDirectory);

        // Made there, with the README that is the feature's whole interface.
        Assert.True(File.Exists(Path.Combine(_portable, "scripts", "README")));
    }

    /// <summary>
    /// **The platform is the hop between the application and the runner, and
    /// nothing held it.** The test above builds the runner by hand; a
    /// platform that built it without the portable folder — the argument
    /// defaults to null — sent a portable copy's scripts back to the machine
    /// with every test green. Read from the source, as LauncherRowTests reads
    /// this constructor, because building a real LinuxPlatform starts a device
    /// watcher, sweeps the user's runtime folder for left-over shares and sets
    /// three process-wide seams.
    /// </summary>
    [Fact]
    public void The_platform_hands_its_portable_folder_to_the_scripts()
        => Assert.Contains(
            "Scripts = new LinuxScriptRunner(portableRoot);",
            RepoSource.Read("src", "Vaktari.Linux", "LinuxPlatform.cs"),
            StringComparison.Ordinal);
}
