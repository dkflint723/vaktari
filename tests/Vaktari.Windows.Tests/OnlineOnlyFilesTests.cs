using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The promises not to download a file a sync client keeps online, measured
/// against real online-only files rather than an attribute set by hand.
///
/// **Until these, every one of those promises was argued, not measured.** The
/// only sync root on the machine they were written on was one nobody may
/// touch, and every file in it was on the disk anyway. <see cref="CloudSyncRoot"/>
/// registers a sync root of the test's own in a temp folder, creates
/// placeholders whose bytes it holds back, and counts each time the cloud
/// files filter asks for them — so "did this download the file" has an answer
/// that does not come from the code being asked about.
///
/// Measured before the fix: content search fetched nothing; the image header
/// read, the thumbnail beside it, the dimensions column and the duplicate
/// finder each fetched every file they were pointed at. The shell's thumbnail
/// for a format Vaktari does not decode fetched nothing either.
///
/// Each refusal is asked again of the same file after it HAS been fetched,
/// and seen to read it: a refusal that refused every placeholder, or every
/// file, would pass the first half and fail the second.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(DuplicateIdentityCollection.Name)]
public sealed class OnlineOnlyFilesTests : IDisposable
{
    private readonly CloudSyncRoot _cloud = CloudSyncRoot.Create();
    private readonly Func<string, bool>? _onlineBefore = OnlineOnly.Test;
    private readonly Func<string, (ulong, ulong, ulong)?>? _identityBefore = DuplicateFinder.Identity;

    public OnlineOnlyFilesTests()
    {
        // Adopted the way WindowsPlatform adopts them. The reparse reader
        // matters most: without it the walk takes every reparse point for a
        // link and never offers a placeholder to the finder at all, which
        // passed the duplicate test here for the wrong reason until it was set.
        SafeWalk.ReparseTag = ReparseTags.Of;
        DuplicateFinder.Identity = FileIdentity.Of;
        OnlineOnly.Test = Placeholders.IsHeldOnline;
    }

    public void Dispose()
    {
        OnlineOnly.Test = _onlineBefore;
        DuplicateFinder.Identity = _identityBefore;
        _cloud.Dispose();
    }

    /// <summary>A PNG header of the given size, padded: enough for ImageSize.</summary>
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[1500];
        byte[] signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
                            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        return bytes;
    }

    private string Fetches() =>
        $"fetched for this process: [{string.Join(", ", _cloud.Fetched)}]; "
        + $"for others: [{string.Join(", ", _cloud.OtherFetches)}]";

    private void AssertNothingFetched(params string[] paths)
    {
        Assert.True(_cloud.FetchCount == 0 && _cloud.OtherFetches.Count == 0, Fetches());

        foreach (var path in paths)
            Assert.True(CloudSyncRoot.IsOnlineOnly(path), $"{Path.GetFileName(path)} is no longer online-only");
    }

    /// <summary>Fetches the file the way any reader would, and checks it was.</summary>
    private void Download(string path)
    {
        var before = _cloud.FetchCount;
        File.ReadAllBytes(path);
        Assert.True(_cloud.FetchCount > before, Fetches());
    }

    /// <summary>
    /// **The positive control: the fixture sees a read fetch the file.** Every
    /// other test here asserts a count of zero, which a fixture that could not
    /// count would satisfy too. A plain read of a placeholder is counted, gets
    /// the bytes the provider holds, and leaves the file on the disk — so
    /// "still online-only" afterwards is a state a read really does change.
    /// </summary>
    [WindowsFact]
    public void The_fixture_counts_a_read_that_downloads_a_file()
    {
        var photo = _cloud.Placeholder("photo.png", Png(640, 480));

        Assert.True(CloudSyncRoot.IsOnlineOnly(photo), "the placeholder was created with its data on the disk");
        Assert.Equal(0, _cloud.FetchCount);

        Assert.Equal(Png(640, 480), File.ReadAllBytes(photo));

        Assert.Equal(["photo.png"], _cloud.Fetched);
        Assert.False(CloudSyncRoot.IsOnlineOnly(photo), "a read left the file online-only, so that state proves nothing");
    }

    /// <summary>
    /// **The fixture leaves nothing behind**: a sync root of its own while it
    /// lives, and after it, no registration and no folder. A second root, so
    /// the one this class disposes is not the one being asked about.
    /// </summary>
    [WindowsFact]
    public void The_fixture_unregisters_its_root_and_deletes_the_folder()
    {
        var other = CloudSyncRoot.Create();
        var root = other.Root;

        try
        {
            other.Placeholder("left.txt", "behind"u8.ToArray());

            Assert.True(CloudSyncRoot.IsSyncRoot(root), "the root the fixture made is not a sync root");
            Assert.False(CloudSyncRoot.IsSyncRoot(Path.GetTempPath()), "the temp folder itself reads as a sync root");
        }
        finally
        {
            other.Dispose();
        }

        Assert.Equal(0, other.UnregisterResult);
        Assert.False(other.RegisteredAfterDispose, "the root was still registered after CfUnregisterSyncRoot");
        Assert.False(Directory.Exists(root), "the root's folder was left behind");
    }

    /// <summary>
    /// The exposed read names real placeholders, which until now it had only
    /// been argued to. Both kinds a provider leaves: one it has marked in sync
    /// and one it has not.
    /// </summary>
    [WindowsFact]
    public void The_exposed_read_names_real_placeholders()
    {
        _cloud.Placeholder("pending.txt", "not yet uploaded"u8.ToArray());
        _cloud.Placeholder("synced.txt", "kept online"u8.ToArray(), inSync: true);

        Assert.Equal(["pending.txt", "synced.txt"], Placeholders.HeldOnlineIn(_cloud.Root).Order(StringComparer.Ordinal));
        Assert.True(Placeholders.IsHeldOnline(Path.Combine(_cloud.Root, "synced.txt")));
        AssertNothingFetched();
    }

    /// <summary>
    /// **Content search does not open a file kept online**, and says it left
    /// one unread. The file holds the word, so the empty answer can only mean
    /// it was not read — and once it has been downloaded, the same search
    /// finds it.
    /// </summary>
    [WindowsFact]
    public async Task Content_search_does_not_download_a_file_kept_online()
    {
        var notes = _cloud.Placeholder("notes.txt", "remember the milk"u8.ToArray());
        var skipped = new ContentSkips();

        Assert.Empty(await Search("milk", skipped));
        Assert.Equal(1, skipped.Online);
        AssertNothingFetched(notes);

        Download(notes);

        Assert.Equal(["notes.txt"], await Search("milk", new ContentSkips()));
    }

    private async Task<List<string>> Search(string text, ContentSkips skipped)
    {
        var query = new SearchQuery
        {
            Text = text,
            ScopePath = _cloud.Root,
            MatchContent = true,
            Skipped = skipped,
            MaxResults = 50,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var found = new List<string>();

        await foreach (var entry in new WindowsSearchProvider().SearchAsync(query, cts.Token))
            found.Add(entry.Name);

        return found;
    }

    /// <summary>
    /// **The duplicate finder does not download files kept online.** Two
    /// identical placeholders share a length, which is exactly what sends a
    /// pair to be read — measured before the fix: both fetched, and offered as
    /// a set. They are counted as unread instead; once on the disk they are
    /// the set they are.
    /// </summary>
    [WindowsFact]
    public void The_duplicate_finder_does_not_download_files_kept_online()
    {
        var same = Encoding.UTF8.GetBytes(new string('x', 5000));
        var a = _cloud.Placeholder("a.bin", same);
        var b = _cloud.Placeholder("b.bin", same);

        var report = DuplicateFinder.Find(_cloud.Root, null, CancellationToken.None);

        AssertNothingFetched(a, b);
        Assert.Empty(report.Sets);
        Assert.Equal(2, report.Unreadable);

        Download(a);
        Download(b);

        var set = Assert.Single(DuplicateFinder.Find(_cloud.Root, null, CancellationToken.None).Sets);
        Assert.Equal([a, b], set.Paths.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// **The image header is not read from a picture kept online.** Thirty-two
    /// bytes, and measured before the fix to fetch the file — under the
    /// fixture's full-hydration policy, all of it. Unknown instead.
    /// </summary>
    [WindowsFact]
    public void The_image_header_is_not_read_from_a_picture_kept_online()
    {
        var photo = _cloud.Placeholder("photo.png", Png(640, 480));

        Assert.Null(ImageSize.TryRead(photo));
        AssertNothingFetched(photo);

        Download(photo);

        Assert.Equal((640, 480), ImageSize.TryRead(photo));
    }

    /// <summary>
    /// **The dimensions column does not download a picture kept online** — it
    /// asks the header read above, per image row in the viewport.
    /// </summary>
    [WindowsFact]
    public async Task The_dimensions_column_does_not_download_a_picture_kept_online()
    {
        var photo = _cloud.Placeholder("photo.png", Png(640, 480));
        var metadata = new WindowsMetadataProvider();

        Assert.Null(await metadata.DescribeAsync(photo, isDirectory: false, CancellationToken.None));
        AssertNothingFetched(photo);

        Download(photo);

        Assert.Equal("640 × 480", await metadata.DescribeAsync(photo, isDirectory: false, CancellationToken.None));
    }

    /// <summary>
    /// **A picture kept online is not handed back to be decoded.** Measured
    /// before the fix: the provider's own size check fetched the file, and it
    /// then returned the path for the loader to open and decode. Asked with
    /// the seam cleared, because the provider answers this from the platform
    /// directly and must not depend on the seam being adopted. No path, and no
    /// pixels either: the shell is not asked for a format Vaktari decodes.
    /// </summary>
    [WindowsFact]
    public async Task The_thumbnail_of_a_picture_kept_online_is_not_decoded()
    {
        OnlineOnly.Test = null;

        var photo = _cloud.Placeholder("photo.png", Png(640, 480));
        var thumbnails = new WindowsThumbnailProvider();

        Assert.Null(await thumbnails.GetThumbnailPathAsync(photo, 256, CancellationToken.None));
        Assert.Null(await thumbnails.GetThumbnailPixelsAsync(photo, 256, CancellationToken.None));
        AssertNothingFetched(photo);

        Download(photo);

        Assert.Equal(photo, await thumbnails.GetThumbnailPathAsync(photo, 256, CancellationToken.None));
    }

    /// <summary>
    /// **The shell's thumbnail for a format Vaktari does not decode fetches
    /// nothing** — measured, not guarded: nothing in Vaktari checks before
    /// this call, and none is added, because the shell itself declines.
    /// Pinned so a change of flags or of route that made it read would show.
    /// Asked synchronously rather than under the provider's two-second bound,
    /// so a fetch the shell made late could not land after the assertion.
    /// .tif because its handler ships with every Windows (see
    /// WindowsShellThumbnailTests); .mp4, .pdf, .docx and .heic measured the
    /// same on the machine this was written on.
    /// </summary>
    [WindowsFact]
    public void The_shell_thumbnail_of_a_file_kept_online_fetches_nothing()
    {
        Assert.True(WindowsShellThumbnails.HasHandler(".tif"), "no thumbnail handler for .tif, so the shell was never asked");

        var scan = _cloud.Placeholder("scan.tif", new byte[20_000]);

        ShellImage.Pixels(scan, 256, ShellImage.ThumbnailOnly, "online-only-test");

        AssertNothingFetched(scan);
    }
}

/// <summary>
/// The classes that set <see cref="DuplicateFinder.Identity"/>, run one at a
/// time.
///
/// **WalkNameTests failed once in twelve full runs beside OnlineOnlyFilesTests**
/// — "Assert.Single() Failure: The collection was empty" — because it clears
/// the seam to see a hard-linked pair offered as a set, and this class's
/// constructor, running for a test in parallel, put the platform's reader
/// back in between. One static, so the two cannot share a moment; the rest of
/// the assembly still runs beside either.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DuplicateIdentityCollection
{
    public const string Name = "DuplicateFinder.Identity";
}
