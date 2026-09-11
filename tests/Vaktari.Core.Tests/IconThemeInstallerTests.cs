using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Fetching a theme from the catalogue, and what happens to a download that
/// is not the file the catalogue names.
///
/// **The archive was unpacked on the strength of its address.** The catalogue
/// pointed at a branch, so the bytes changed daily, and nothing compared what
/// arrived against anything: a poisoned download — a hijacked repository, a
/// tampered mirror — would have been unpacked into the icon folder and put in
/// use. The catalogue now names a release and its SHA-256, the download is
/// digested on its way past the unpacker, and a mismatch is thrown away with
/// the staging folder before a theme folder has been touched.
///
/// The network is a handler seam serving an archive built here, and the
/// install root is redirected — a test of this must never write into the
/// developer's own icon folder.
/// </summary>
[Collection("icon index cache")]
public sealed class IconThemeInstallerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-fetch-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly Serve _server = new();

    public IconThemeInstallerTests()
    {
        Directory.CreateDirectory(_root);
        IconThemeCatalogue.InstallRootOverride = _root;
        IconThemeInstaller.HandlerOverride = _server;
    }

    public void Dispose()
    {
        IconThemeCatalogue.InstallRootOverride = null;
        IconThemeInstaller.HandlerOverride = null;

        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // ---- the pieces ----------------------------------------------------------

    /// <summary>The network: answers every request with one body.</summary>
    private sealed class Serve : HttpMessageHandler
    {
        public byte[] Body { get; set; } = [];
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Body),
            });
        }
    }

    /// <summary>The smallest thing the unpacker accepts as a theme.</summary>
    private static byte[] Archive()
    {
        var raw = new MemoryStream();

        using (var gzip = new GZipStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "pack-1.0/"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "pack-1.0/Mini/"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "pack-1.0/Mini/places/"));

            var index = new PaxTarEntry(TarEntryType.RegularFile, "pack-1.0/Mini/index.theme")
            {
                DataStream = new MemoryStream("[Icon Theme]\nName=Mini\n"u8.ToArray()),
            };
            tar.WriteEntry(index);

            var icon = new PaxTarEntry(TarEntryType.RegularFile, "pack-1.0/Mini/places/folder.svg")
            {
                DataStream = new MemoryStream("<svg/>"u8.ToArray()),
            };
            tar.WriteEntry(icon);
        }

        return raw.ToArray();
    }

    private static string Sha256Of(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static IconThemeSource Source(string sha256) => new(
        "Pack", "a theme for the test", "https://example.test/pack-1.0.tar.gz", 1, "MIT", sha256);

    private string Landed => Path.Combine(_root, "Pack", "Mini");

    // ---- what it should do ---------------------------------------------------

    [Fact]
    public async Task A_download_that_hashes_as_the_catalogue_says_is_installed()
    {
        _server.Body = Archive();

        var installed = await IconThemeInstaller.InstallAsync(Source(Sha256Of(_server.Body)));

        Assert.Equal(["https://example.test/pack-1.0.tar.gz"], _server.Requested);
        Assert.Equal([Landed], installed.Themes);
        Assert.True(File.Exists(Path.Combine(Landed, "index.theme")));
        Assert.True(File.Exists(Path.Combine(Landed, "places", "folder.svg")));
    }

    /// <summary>
    /// The archive is valid — every entry unpacks — and it is still refused,
    /// because it is not the archive the catalogue names. Nothing reaches the
    /// pack folder: the mismatch is found before publishing, and the staging
    /// tree goes with it.
    /// </summary>
    [Fact]
    public async Task A_download_that_hashes_to_anything_else_is_thrown_away_before_it_is_published()
    {
        _server.Body = Archive();

        var refused = await Assert.ThrowsAsync<InvalidDataException>(() =>
            IconThemeInstaller.InstallAsync(Source(Sha256Of([0xba, 0xad]))));

        Assert.Contains("not the Pack the catalogue expects", refused.Message);
        Assert.Contains("nothing was installed", refused.Message);

        Assert.False(Directory.Exists(Landed), "the mismatched theme was published anyway");

        var pack = Path.Combine(_root, "Pack");
        Assert.Empty(Directory.Exists(pack) ? Directory.GetFileSystemEntries(pack) : []);
    }

    /// <summary>
    /// **The digest is of the whole download, not of the part the unpacker
    /// needed.** A tar reader stops at the end-of-archive marker, and what
    /// follows — the gzip trailer, a second member, padding — is never read on
    /// its own account. Here a quarter of a megabyte of it follows, larger than
    /// any buffer between the network and the reader, and the hash the
    /// catalogue carries is of all of it: the install must still match.
    /// </summary>
    [Fact]
    public async Task The_hash_covers_the_whole_download_including_what_the_unpacker_never_asked_for()
    {
        var trailing = new MemoryStream();
        using (var gzip = new GZipStream(trailing, CompressionLevel.NoCompression, leaveOpen: true))
            gzip.Write(RandomNumberGenerator.GetBytes(256 * 1024));

        _server.Body = [.. Archive(), .. trailing.ToArray()];

        var installed = await IconThemeInstaller.InstallAsync(Source(Sha256Of(_server.Body)));

        Assert.Equal([Landed], installed.Themes);
    }

    /// <summary>
    /// The catalogue's own entries: a release, never a branch — a branch is a
    /// different file every day and no hash can name it — and a hash of the
    /// right shape beside each URL.
    /// </summary>
    [Fact]
    public void Every_catalogue_entry_names_a_release_and_its_hash()
    {
        Assert.NotEmpty(IconThemeCatalogue.All);

        foreach (var source in IconThemeCatalogue.All)
        {
            Assert.DoesNotContain("/refs/heads/", source.Url);
            Assert.Matches("^[0-9a-f]{64}$", source.Sha256);
        }
    }
}
