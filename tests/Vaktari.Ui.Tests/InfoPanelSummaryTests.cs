using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the docked details panel says about the size of one selected row.
///
/// **The panel describes what the listing is showing, so it cannot be emptier
/// than the listing.** A folder has no size until something works it out, and
/// blanking every folder was right while that was true — in a listing that
/// measured its rows it left the panel silent about the one number the row was
/// there to carry.
///
/// No provider: the summary is filled from what the listing already knows,
/// before any stat is in flight, which is the half these tests are about.
/// </summary>
public sealed class InfoPanelSummaryTests
{
    private static FileEntry Folder(string name)
        => new(name, "/here/" + name, 0, DateTimeOffset.UnixEpoch, EntryFlags.Directory);

    private static FileEntry Measured(string name, long bytes)
        => new(name, "/here/" + name, bytes, DateTimeOffset.UnixEpoch,
               EntryFlags.Directory | EntryFlags.Measured);

    private static FileEntry File(string name, long bytes)
        => new(name, "/here/" + name, bytes, DateTimeOffset.UnixEpoch, EntryFlags.None);

    [Fact]
    public async Task A_measured_folder_reports_what_is_inside_it()
    {
        var panel = new InfoPanelViewModel(null);

        await panel.ShowAsync(Measured("photos", 30));

        Assert.Equal("30 B", panel.Summary);
    }

    /// <summary>The half that says the rule above did not swallow every folder:
    /// an ordinary one still has no size to report.</summary>
    [Fact]
    public async Task An_ordinary_folder_reports_nothing()
    {
        var panel = new InfoPanelViewModel(null);

        await panel.ShowAsync(Folder("photos"));

        Assert.Equal("", panel.Summary);
    }

    /// <summary>And a file is unchanged by any of it.</summary>
    [Fact]
    public async Task A_file_still_reports_its_own_size()
    {
        var panel = new InfoPanelViewModel(null);

        await panel.ShowAsync(File("notes.txt", 30));

        Assert.Equal("30 B", panel.Summary);
    }
}
