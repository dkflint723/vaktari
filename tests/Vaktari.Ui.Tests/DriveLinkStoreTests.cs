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

    [Fact]
    public void Saving_empty_clears_the_file_for_next_time()
    {
        var store = new JsonDriveLinkStore(_root);

        store.Save([new DriveLink(@"D:\x\a", "/my-files/a", "https://u")]);
        store.Save([]);

        Assert.Empty(new JsonDriveLinkStore(_root).Load());
    }
}
