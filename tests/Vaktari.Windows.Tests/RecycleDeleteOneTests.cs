using System.Runtime.Versioning;
using System.Text;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Destroying ONE thing in the Recycle Bin.
///
/// **The only routes out were Restore and Empty.** So the answer to "I want
/// that one gone for good" was to empty the whole bin, and Shift+Delete on a
/// bin row asked the permanent-delete question, took the yes, and then refused:
/// a bin row carries the path the file USED to occupy, which the file
/// operations cannot act on. Explorer deletes just the items you picked.
///
/// The key is the $I metadata path, which is what this bin calls a trash name —
/// two volumes' bins can hold the same $I filename, and the one that was picked
/// must not depend on which was found first.
///
/// A made-up pair under a temp directory, exactly as
/// <see cref="RecycleBinPurgeTests"/> does: the parsing and the purge do not
/// care that the folder is not called $Recycle.Bin, and nothing in this file
/// touches the bin of whoever is running it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RecycleDeleteOneTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-delone-" + Guid.NewGuid().ToString("N")[..8]);

    public RecycleDeleteOneTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>A version 2 record — the shape Windows 10 and later write.</summary>
    private static byte[] Record(string original, long size, DateTimeOffset deleted)
    {
        var chars = Encoding.Unicode.GetBytes(original);
        var bytes = new byte[28 + chars.Length + 2];

        BitConverter.TryWriteBytes(bytes.AsSpan(0), 2L);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), size);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), deleted.ToFileTime());
        BitConverter.TryWriteBytes(bytes.AsSpan(24), original.Length + 1);
        chars.CopyTo(bytes, 28);

        return bytes;
    }

    /// <summary>One recycled item: its metadata, and the payload beside it.</summary>
    private string Recycled(string tag, string original)
    {
        var info = Path.Combine(_root, "$I" + tag + ".txt");
        var payload = Path.Combine(_root, "$R" + tag + ".txt");

        File.WriteAllBytes(info, Record(original, 7, DateTimeOffset.Now.AddDays(-1)));
        File.WriteAllText(payload, "payload");

        return info;
    }

    private static string PayloadOf(string infoPath)
        => RecycleBin.PayloadFor(infoPath)
           ?? throw new InvalidOperationException("no payload for " + infoPath);

    /// <summary>The whole finding: one goes, the other stays.</summary>
    [WindowsFact]
    public void One_item_can_be_destroyed_without_emptying_the_rest()
    {
        var going = Recycled("aaa", @"C:\Users\me\notes.txt");
        var staying = Recycled("bbb", @"C:\Users\me\keep.txt");

        new WindowsTrashMaintenance().Delete(going);

        Assert.False(File.Exists(going), "its metadata should be gone");
        Assert.False(File.Exists(PayloadOf(going)), "and the payload with it");

        Assert.True(File.Exists(staying), "nothing else should have been touched");
        Assert.True(File.Exists(PayloadOf(staying)));
    }

    /// <summary>
    /// **Something already gone is not an error.** The bin is shared with
    /// Explorer and everything else on the machine, so between the click and
    /// the delete somebody else may have taken it — and a throw here would
    /// surface as a failure to destroy a file that is already destroyed.
    /// </summary>
    [WindowsFact]
    public void An_item_that_is_no_longer_there_is_not_an_error()
    {
        var staying = Recycled("ccc", @"C:\Users\me\keep.txt");

        new WindowsTrashMaintenance().Delete(Path.Combine(_root, "$Ivanished.txt"));

        Assert.True(File.Exists(staying));
    }

    /// <summary>
    /// **Metadata whose payload is gone is left exactly where the listing
    /// leaves it.** An orphaned $I is not a row: Read refuses it, so it never
    /// appears in the bin and nobody can select it. Destroying it here would be
    /// this one method tidying up a state that the listing, Restore and Empty
    /// all agree to leave alone — decided from the one place with no undo.
    /// </summary>
    [WindowsFact]
    public void Metadata_without_a_payload_is_not_a_row_and_is_not_touched()
    {
        var info = Recycled("ddd", @"C:\Users\me\notes.txt");

        File.Delete(PayloadOf(info));

        // Against Read rather than List: List walks the real bin of whoever is
        // running this, where a temp directory was never going to appear, so
        // asserting its absence there would pass for the wrong reason. Read is
        // the step that makes an orphan invisible.
        Assert.Null(RecycleBin.Read(info));

        new WindowsTrashMaintenance().Delete(info);

        Assert.True(File.Exists(info));
    }

    /// <summary>And it is reachable through the interface, which is all the
    /// pane holds.</summary>
    [WindowsFact]
    public void The_bin_offers_it_through_its_interface()
    {
        var info = Recycled("eee", @"C:\Users\me\notes.txt");

        ITrashMaintenance bin = new WindowsTrashMaintenance();

        bin.Delete(info);

        Assert.False(File.Exists(info));
    }
}
