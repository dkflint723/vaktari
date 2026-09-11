using System.Net.Http;
using System.Security.Cryptography;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// Fetches a theme and unpacks it, so that choosing one is a button rather than
/// a download, an extraction, and a folder picker.
///
/// **The extraction is the reason this exists.** Papirus is built out of some
/// forty thousand symbolic links, Windows creates none of them without
/// Developer Mode, and every ordinary unzipping tool therefore fails forty
/// thousand times and leaves a theme with holes where the file and folder icons
/// should be. Unpacking it here means the links are read rather than made — see
/// <see cref="IconThemeArchive"/>, which is also where the containment and size
/// rules live.
/// </summary>
public static class IconThemeInstaller
{
    /// <summary>
    /// Generous, because these are large files on whatever connection somebody
    /// has, and stingier than none, because a stalled download should not hang
    /// a settings window forever. Applies to the whole transfer, which is why
    /// it is minutes rather than seconds.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);

    /// <summary>Stands in for the network in tests, which serve an archive of
    /// their own making. Null in production.</summary>
    internal static HttpMessageHandler? HandlerOverride { get; set; }

    /// <summary>
    /// Downloads and installs, reporting progress from 0 to 1 where the server
    /// says how large the file is.
    ///
    /// **Streamed straight into the unpacking** rather than saved and then
    /// read: a hundred and ten megabytes is not worth writing twice, and a
    /// transfer that fails halfway leaves nothing behind because the unpacking
    /// stages its work and publishes only at the end.
    /// </summary>
    public static async Task<IconThemeArchive.Installed> InstallAsync(
        IconThemeSource source,
        IProgress<FetchProgress>? progress = null,
        CancellationToken token = default)
    {
        using var http = new HttpClient(HandlerOverride ?? new HttpClientHandler(), disposeHandler: HandlerOverride is null)
        {
            Timeout = Patience,
        };

        // Courtesy, not a requirement — and it was written down as a
        // requirement, which the next person to read it would have believed.
        // GitHub does redirect these to codeload.github.com, but it answers 200
        // with no User-Agent header at all. Still sent, because a host that has
        // to guess who is asking is entitled to a name.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vaktari");

        using var response = await http
            .GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var expected = response.Content.Headers.ContentLength;

        await using var network = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var counted = new CountingStream(network, expected, progress);

        // **Hashed as it streams, compared before anything is published.** The
        // archive unpacks straight off the network and never sits on disk
        // whole, so the bytes are digested on their way past and the digest is
        // read once the last of them has gone by — which the unpacker does
        // not guarantee on its own, a tar reader stops at the end-of-archive
        // marker and leaves the gzip trailer unread, hence the drain. HTTPS
        // says who sent the file; this says it is the file the catalogue was
        // written against, and a mismatch is thrown away with the staging
        // folder, before a theme folder has been touched.
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var hashed = new HashingStream(counted, digest);
        var destination = IconThemeCatalogue.FolderFor(source);

        // Off the calling thread: unpacking fifty thousand files is real work,
        // and the caller is a settings window.
        return await Task
            .Run(() => IconThemeArchive.Install(hashed, destination, token, beforePublish: () =>
            {
                hashed.DrainToEnd(token);

                var actual = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();

                if (!string.Equals(actual, source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"the download is not the {source.Name} the catalogue expects "
                        + $"(sha256 {actual[..12]}…, wanted {source.Sha256[..12]}…) — nothing was installed");
            }), token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Installs an archive already on disk — one downloaded from somewhere this
    /// list does not carry.
    ///
    /// **Through exactly the same unpacking**, which is the whole reason to
    /// offer it: a theme from anywhere hits the same symbolic-link wall, and
    /// the containment, whitelist and size rules apply to a chosen file no less
    /// than to a fetched one. The format is read from the file's first bytes,
    /// so .tar.gz, .tar.xz and .zip all work whatever they happen to be called.
    ///
    /// Its own folder, named after the file, so two downloads cannot overwrite
    /// each other's themes.
    /// </summary>
    public static async Task<IconThemeArchive.Installed> InstallFromFileAsync(
        string file, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(file);

        var destination = Path.Combine(IconThemeCatalogue.InstallRoot, PackName(file));

        return await Task
            .Run(() => IconThemeArchive.Install(stream, destination, token), token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The file's name without the archive extensions — including the double
    /// one, so papirus.tar.gz becomes papirus rather than papirus.tar.
    /// </summary>
    internal static string PackName(string file)
    {
        var name = Path.GetFileName(file);

        foreach (var suffix in new[]
                 { ".tar.gz", ".tar.xz", ".tgz", ".txz", ".tar", ".zip", ".gz", ".xz" })
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            name = name[..^suffix.Length];
            break;
        }

        // A name of nothing would put the themes in the root of the install
        // folder, mixed in with the packs.
        return name.Length > 0 ? name : "icons";
    }

    /// <summary>
    /// Feeds every byte read through it to a digest. Read-only and forward-only
    /// like the counter it wraps.
    ///
    /// **Owns nothing, and disposing it does nothing.** The archive reader
    /// closes the stream it is handed when its decompressor is disposed, and
    /// the bytes past the end-of-archive marker still have to be read for the
    /// digest to be of the whole file; so this wrapper ignores the close, and
    /// the counting stream underneath is closed by whoever opened the network.
    /// </summary>
    internal sealed class HashingStream(Stream inner, IncrementalHash digest) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);

            if (read > 0) digest.AppendData(buffer[..read]);

            return read;
        }

        /// <summary>Reads whatever is left, so the digest covers all of it.</summary>
        public void DrainToEnd(CancellationToken token)
        {
            Span<byte> sink = stackalloc byte[16 * 1024];

            while (Read(sink) > 0) token.ThrowIfCancellationRequested();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) { /* see above */ }
    }

    /// <summary>
    /// Counts what passes through, so the window can show how far along a
    /// download is. Read-only and forward-only, which is all the archive reader
    /// asks for.
    ///
    /// Internal rather than private so the reporting can be tested without a
    /// network. The defect it carried — reporting nothing whatsoever when the
    /// server sends no length — is invisible from outside and is the ordinary
    /// case for the one theme in the catalogue.
    /// </summary>
    internal sealed class CountingStream(Stream inner, long? expected, IProgress<FetchProgress>? progress)
        : Stream
    {
        private long _read;
        private long _lastReported = -1;

        public override int Read(byte[] buffer, int offset, int count)
            => Report(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Report(inner.Read(buffer));

        private int Report(int read)
        {
            // **No early return when the length is unknown**, which is what
            // this used to do — and GitHub, which serves the one theme in the
            // catalogue, never sends a length. The bar sat at zero for a
            // hundred and ten megabytes and read as a hung download.
            if (read <= 0) return read;

            _read += read;

            // Throttled either way, for the reason it always was: a bar told a
            // hundred thousand times is a hundred thousand dispatcher posts and
            // a slower download. Whole percents where there is a total to take
            // a percent of, whole megabytes where there is not.
            var step = expected is { } total && total > 0
                ? _read * 100 / total
                : _read / (1024 * 1024);

            if (step == _lastReported) return read;

            _lastReported = step;
            progress?.Report(new FetchProgress(_read, expected));

            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => expected ?? throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
