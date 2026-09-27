using System.Diagnostics;

namespace Vaktari.Core.Tests;

/// <summary>
/// Links made by the platform's own tool rather than by anything under test —
/// setup sharing an implementation with its subject passes just as happily
/// when both are wrong. The same reason, and the same command, as the Windows
/// suite's TempTree.
///
/// **A junction rather than a symbolic link on Windows**, because
/// <see cref="Directory.CreateSymbolicLink"/> needs Developer Mode or
/// elevation, and a test that skips itself on an ordinary Windows box proves
/// nothing there. Moved here from ArchiveVerbTests when the extraction tests
/// needed the same helper.
/// </summary>
internal static class TestLinks
{
    internal static void Junction(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var mklink = Process.Start(new ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{path}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        mklink.WaitForExit();

        if (!Directory.Exists(path))
            throw new InvalidOperationException(
                $"could not make a junction at '{path}': {mklink.StandardError.ReadToEnd().Trim()}");
    }

    /// <summary>A link to a folder, however this platform makes one without
    /// privilege: a junction on Windows, a symbolic link elsewhere.</summary>
    internal static void FolderLink(string path, string target)
    {
        if (OperatingSystem.IsWindows()) Junction(path, target);
        else Directory.CreateSymbolicLink(path, target);
    }
}
