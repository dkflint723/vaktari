using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Destroying ONE thing in the trash.
///
/// **The only routes out were Restore and Empty.** So the answer to "I want
/// that one gone for good" was to empty the whole bin, and Shift+Delete on a
/// bin row asked the permanent-delete question, took the yes, and then refused:
/// a bin row carries the path the file USED to occupy, which the file
/// operations cannot act on. Both references delete just the items you picked.
///
/// Against a trash of this test's own making — <c>XDG_DATA_HOME</c> is what the
/// spec says decides where the trash lives, so pointing it at a temp directory
/// is the supported way to have one, not a seam cut for testing. Nothing here
/// goes anywhere near the trash of whoever is running it.
/// </summary>
public sealed class TrashDeleteOneTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "vaktari-trashdel-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string? _before = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public TrashDeleteOneTests()
    {
        Directory.CreateDirectory(Files);
        Directory.CreateDirectory(Info);

        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _before);

        try { Directory.Delete(_home, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string Files => Path.Combine(_home, "Trash", "files");
    private string Info => Path.Combine(_home, "Trash", "info");

    /// <summary>One item in this trash, the way any desktop would write it.</summary>
    private void Trashed(string name, string from, string when = "2026-09-01T10:00:00")
    {
        File.WriteAllText(Path.Combine(Files, name), "payload of " + name);

        File.WriteAllText(
            Path.Combine(Info, name + ".trashinfo"),
            $"[Trash Info]\nPath={from}\nDeletionDate={when}\n");
    }

    private static XdgTrashMaintenance Bin() => new();

    /// <summary>The whole finding: one goes, the other stays.</summary>
    [Fact]
    public void One_item_can_be_destroyed_without_emptying_the_rest()
    {
        Trashed("notes.txt", "/home/me/notes.txt");
        Trashed("keep.txt", "/home/me/keep.txt");

        var bin = Bin();

        Assert.Equal(2, bin.List().Count);

        bin.Delete("notes.txt");

        var left = Assert.Single(bin.List());

        Assert.Equal("keep.txt", left.TrashName);
    }

    /// <summary>
    /// **Both files, not just the one you can see.** An item is a payload AND a
    /// sidecar; removing only the payload leaves an orphan info file that every
    /// other file manager on the machine still reads.
    /// </summary>
    [Fact]
    public void Its_sidecar_goes_with_it()
    {
        Trashed("notes.txt", "/home/me/notes.txt");

        Bin().Delete("notes.txt");

        Assert.False(File.Exists(Path.Combine(Files, "notes.txt")));
        Assert.False(File.Exists(Path.Combine(Info, "notes.txt.trashinfo")));
    }

    /// <summary>A trashed folder goes whole, not one level of it.</summary>
    [Fact]
    public void A_folder_goes_with_everything_under_it()
    {
        var payload = Path.Combine(Files, "project");

        Directory.CreateDirectory(Path.Combine(payload, "src"));
        File.WriteAllText(Path.Combine(payload, "src", "main.c"), "int main(){}");

        File.WriteAllText(
            Path.Combine(Info, "project.trashinfo"),
            "[Trash Info]\nPath=/home/me/project\nDeletionDate=2026-09-01T10:00:00\n");

        Bin().Delete("project");

        Assert.False(Directory.Exists(payload));
        Assert.Empty(Bin().List());
    }

    /// <summary>
    /// **Something already gone is not an error.** The bin is shared with every
    /// other program on the machine, so between the click and the delete
    /// somebody else may have taken it — and a throw here would surface as a
    /// failure to destroy a file that is already destroyed.
    /// </summary>
    [Fact]
    public void An_item_that_is_no_longer_there_is_not_an_error()
    {
        Trashed("notes.txt", "/home/me/notes.txt");

        var bin = Bin();

        bin.Delete("nothing-by-that-name");

        Assert.Single(bin.List());
    }

    /// <summary>
    /// And it is reachable through the interface, which is all the pane holds.
    /// </summary>
    [Fact]
    public void The_bin_offers_it_through_its_interface()
    {
        Trashed("notes.txt", "/home/me/notes.txt");

        ITrashMaintenance bin = Bin();

        bin.Delete(bin.List()[0].TrashName);

        Assert.Empty(bin.List());
    }
}
