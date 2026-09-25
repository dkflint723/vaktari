using System.Runtime.Versioning;
using Vaktari.Core.Sharing;
using Vaktari.Ui.Settings;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The remembered drive links. A Proton link outlives the process, so the
/// sidebar's kill switch depends on this file surviving a restart — a link you
/// cannot see is a link you cannot revoke.
/// </summary>
public sealed class DriveLinkStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-drivelinks-" + Guid.NewGuid().ToString("N"));

    public DriveLinkStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void What_was_saved_comes_back_across_a_restart()
    {
        var store = new JsonDriveLinkStore(_root);

        store.Save(
        [
            new DriveLink(@"D:\Proton-Drive\a.txt", "/my-files/a.txt", "https://drive.proton.me/urls/A#k"),
            new DriveLink(@"D:\Proton-Drive\photos", "/my-files/photos", "https://drive.proton.me/urls/B#k"),
        ]);

        // A fresh store stands in for the next launch.
        var reloaded = new JsonDriveLinkStore(_root).Load();

        Assert.Equal(2, reloaded.Count);
        Assert.Equal("/my-files/a.txt", reloaded[0].RemotePath);
        Assert.Equal("https://drive.proton.me/urls/B#k", reloaded[1].Url);
    }

    [Fact]
    public void Nothing_saved_is_an_empty_list()
        => Assert.Empty(new JsonDriveLinkStore(_root).Load());

    /// <summary>A scribbled-on file is an empty sidebar section, not a crash —
    /// the links still exist at Proton and the web app can manage them.</summary>
    [Fact]
    public void A_damaged_file_reads_as_empty()
    {
        File.WriteAllText(Path.Combine(_root, "drive-links.json"), "{not json");

        Assert.Empty(new JsonDriveLinkStore(_root).Load());
    }

    // ---- whose file it is ---------------------------------------------------

    /// <summary>For <c>SkipUnless</c>: the modes below are a Linux fact.</summary>
    public static bool IsLinux => OperatingSystem.IsLinux();

    private const string LinuxOnly = "Asserts Unix file modes; runs on Linux only.";

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private string LinksFile => Path.Combine(_root, "drive-links.json");

    /// <summary>
    /// **Every URL in this file carries its decryption key, and the file was
    /// 0644** — the default create mode, readable by any account that could
    /// reach the state folder.
    /// </summary>
    [Fact(Skip = LinuxOnly, SkipUnless = nameof(IsLinux), SkipType = typeof(DriveLinkStoreTests))]
    [UnsupportedOSPlatform("windows")]
    public void A_saved_file_is_readable_by_its_owner_alone()
    {
        new JsonDriveLinkStore(_root).Save([new DriveLink("/home/me/a", "/my-files/a", "https://u#key")]);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(LinksFile));
    }

    /// <summary>
    /// An open temp left by a crashed write under the older build. The create
    /// mode applies only to a file that is created, so reusing the leftover
    /// would carry its mode through the rename; it is deleted first.
    /// </summary>
    [Fact(Skip = LinuxOnly, SkipUnless = nameof(IsLinux), SkipType = typeof(DriveLinkStoreTests))]
    [UnsupportedOSPlatform("windows")]
    public void A_leftover_open_temp_does_not_lend_the_file_its_mode()
    {
        // A mode no fresh file is given, so the one that arrives can only be
        // the leftover's if it was reused.
        const UnixFileMode leftover = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                                      | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;

        var temp = LinksFile + ".tmp";
        File.WriteAllText(temp, "[]");
        File.SetUnixFileMode(temp, leftover);

        new JsonDriveLinkStore(_root).Save([new DriveLink("/home/me/a", "/my-files/a", "https://u#key")]);

        // Before anything reads it: a read tightens the file, and would hide
        // a save that had carried the leftover's mode across.
        Assert.NotEqual(leftover, File.GetUnixFileMode(LinksFile));
        Assert.Single(new JsonDriveLinkStore(_root).Load());
    }

    /// <summary>A file the older build wrote is tightened the first time it is
    /// read — before any save, which may not come for weeks.</summary>
    [Fact(Skip = LinuxOnly, SkipUnless = nameof(IsLinux), SkipType = typeof(DriveLinkStoreTests))]
    [UnsupportedOSPlatform("windows")]
    public void An_open_file_from_before_is_closed_when_it_is_read()
    {
        File.WriteAllText(
            LinksFile,
            """[{"LocalPath":"/home/me/a","RemotePath":"/my-files/a","Url":"https://u#key"}]""");
        File.SetUnixFileMode(LinksFile, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.Single(new JsonDriveLinkStore(_root).Load());
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(LinksFile));
    }

    [Fact]
    public void Saving_empty_clears_the_file_for_next_time()
    {
        var store = new JsonDriveLinkStore(_root);

        store.Save([new DriveLink(@"D:\x\a", "/my-files/a", "https://u")]);
        store.Save([]);

        Assert.Empty(new JsonDriveLinkStore(_root).Load());
    }
}
