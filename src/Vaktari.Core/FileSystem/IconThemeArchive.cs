using System.Formats.Tar;
using System.IO.Compression;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// Unpacks a downloaded icon theme, safely.
///
/// **This exists because Windows will not make the symbolic links a theme is
/// built out of.** Papirus expresses its whole structure in about forty
/// thousand of them — Papirus-Dark is very nearly nothing but links back into
/// Papirus — and creating one needs Developer Mode or an elevated process. Any
/// ordinary extraction therefore fails forty thousand times, says so at length,
/// and leaves a theme with holes exactly where files and folders are.
///
/// Doing the unpacking here rather than handing the archive to the shell is what
/// removes that: a link becomes a line in a small index Vaktari reads, which
/// needs no privilege at all and costs nothing on disk. See
/// <see cref="AliasIndex"/>.
///
/// **Nothing that is not an icon comes out of the archive.** A theme is
/// index.theme, .svg and .png; everything else in the file — scripts, makefiles,
/// anything at all — is skipped rather than written and then ignored. That, the
/// containment check on every path, and the size caps are the whole safety
/// story, and they are deliberately boring: this reads an archive off the
/// internet.
/// </summary>
public static class IconThemeArchive
{
    /// <summary>The per-theme file that carries what the symbolic links meant.
    /// One line per alias, tab separated, both sides relative to the theme
    /// folder it sits in.</summary>
    public const string AliasIndex = ".vaktari-aliases";

    /// <summary>
    /// Bounds, so a malformed or hostile archive cannot fill a disk. Papirus is
    /// the largest of the common themes by a distance — roughly 51,000 entries
    /// and 110 MB — so these leave it several times over rather than fitting it
    /// exactly.
    /// </summary>
    private const int MaxEntries = 250_000;
    private const long MaxTotalBytes = 512L * 1024 * 1024;
    private const long MaxEntryBytes = 32L * 1024 * 1024;

    public sealed record Installed(IReadOnlyList<string> Themes, int Icons, int Aliases, long Bytes);

    /// <summary>
    /// Reads a .tar.gz and leaves the themes inside it in <paramref name="destination"/>,
    /// one folder each.
    ///
    /// **Unpacked beside the destination and moved in at the end**, so a
    /// download that fails halfway cannot leave a half-theme behind — a theme
    /// with some of its icons is worse than no theme, because it looks like it
    /// worked.
    /// </summary>
    /// <param name="beforePublish">Runs after the archive has been read in
    /// full and before anything is moved into the destination; throwing here
    /// discards the unpacked tree. The fetch uses it to compare the download
    /// against the hash in the catalogue — **there is no earlier point**, the
    /// archive streams straight off the network, and no later one that would
    /// not already have published.</param>
    public static Installed Install(
        Stream archive, string destination, CancellationToken token = default, Action? beforePublish = null)
    {
        // **Normalised once, here, and everything downstream is then comparable.**
        // Paths are compared against each other all through this — is this entry
        // under the destination, is this link inside the theme — and one side of
        // those comparisons has been through GetFullPath while the other has
        // not. A destination written with forward slashes, which Windows accepts
        // everywhere, therefore matched nothing: every one of Papirus's fifty
        // thousand links was silently dropped and the theme came out with holes,
        // which is the exact failure this class exists to prevent.
        destination = Path.GetFullPath(destination);

        Directory.CreateDirectory(destination);

        // A sibling of the destination rather than the system temp folder: the
        // last step is a directory move, and a move across volumes is a copy of
        // every one of fifty thousand files.
        var staging = Path.Combine(destination, ".unpacking-" + Guid.NewGuid().ToString("N")[..12]);

        try
        {
            var (icons, bytes, aliases) = Unpack(archive, staging, token);

            beforePublish?.Invoke();

            var themes = Publish(staging, destination, aliases, token);

            return new Installed(themes, icons, aliases.Count, bytes);
        }
        finally
        {
            Delete(staging);
        }
    }

    /// <summary>
    /// One entry, whichever kind of archive it came out of.
    /// </summary>
    /// <param name="Name">Its path inside the archive, unverified.</param>
    /// <param name="Kind">What it is.</param>
    /// <param name="Length">What it claims to unpack to, or -1 if it does not say.</param>
    /// <param name="Link">Where a link points, relative to the folder it sits in.</param>
    /// <param name="CopyInto">Writes its content, called only for a file being
    /// kept. An action rather than a stream to dispose, because the two formats
    /// disagree: a zip entry's stream must be closed and a tar entry's belongs
    /// to the reader and must not be.</param>
    private sealed record Entry(
        string Name, EntryKind Kind, long Length, string? Link, Action<Stream> CopyInto);

    private enum EntryKind { File, Folder, Link, Other }

    /// <summary>
    /// **Read from the file's first bytes, not its name.** Somebody who
    /// downloaded a theme themselves may hand over anything, and a .zip renamed
    /// to .tar.gz is a confusing error rather than an obvious one.
    /// </summary>
    private static IEnumerable<Entry> Read(Stream archive, CancellationToken token)
    {
        // Six, because xz's signature is the longest of the three.
        Span<byte> magic = stackalloc byte[6];

        var buffered = new BufferedStream(archive, 8192);
        var read = buffered.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);

        var gzip = read >= 2 && magic[0] == 0x1f && magic[1] == 0x8b;

        var zip = read >= 4 && magic[0] == 'P' && magic[1] == 'K' && magic[2] == 3 && magic[3] == 4;

        var xz = read >= 6 && magic[0] == 0xfd && magic[1] == '7' && magic[2] == 'z'
            && magic[3] == 'X' && magic[4] == 'Z' && magic[5] == 0x00;

        if (!gzip && !zip && !xz)
            throw new InvalidDataException(
                "that is not a .tar.gz, .tar.xz or .zip. "
                + "An icon theme is usually published as one of those.");

        // The magic has been consumed and none of the readers can seek back for
        // it, so it is put in front again.
        var whole = new ConcatStream(magic[..read].ToArray(), buffered);

        if (zip) return FromZip(whole, token);

        // A factory rather than a stream, so the decompressor is created when
        // the entries are actually walked and disposed with them.
        return FromTar(
            gzip
                ? () => new GZipStream(whole, CompressionMode.Decompress)
                : () => new SharpCompress.Compressors.Xz.XZStream(whole),
            token);
    }

    /// <summary>
    /// A plain tar, however it was compressed. Both wrappers produce one, and
    /// tar is the format that records the symbolic links a theme is built from.
    /// </summary>
    private static IEnumerable<Entry> FromTar(Func<Stream> open, CancellationToken token)
    {
        using var decompressed = open();
        using var tar = new TarReader(decompressed);

        while (tar.GetNextEntry() is { } entry)
        {
            token.ThrowIfCancellationRequested();

            var kind = entry.EntryType switch
            {
                TarEntryType.Directory => EntryKind.Folder,
                TarEntryType.SymbolicLink or TarEntryType.HardLink => EntryKind.Link,
                TarEntryType.RegularFile or TarEntryType.V7RegularFile => EntryKind.File,
                _ => EntryKind.Other,
            };

            var current = entry;

            yield return new Entry(
                entry.Name, kind, entry.Length, entry.LinkName,
                to =>
                {
                    if (current.DataStream is { } from) Copy(from, to, current.Name);
                });
        }
    }

    /// <summary>
    /// **A zip carries no links**, so a theme from one arrives with whatever
    /// its publisher chose to duplicate and nothing else. That is not a reason
    /// to refuse it: the reader falls back to the theme a variant is named
    /// after, which covers the case a zip loses.
    /// </summary>
    private static IEnumerable<Entry> FromZip(Stream archive, CancellationToken token)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read);

        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();

            // A directory is an entry whose name ends in a separator and which
            // has nothing in it; every other directory is implied by its files.
            var folder = entry.Name.Length == 0;

            var current = entry;

            yield return new Entry(
                entry.FullName,
                folder ? EntryKind.Folder : EntryKind.File,
                entry.Length,
                null,
                to =>
                {
                    using var from = current.Open();

                    Copy(from, to, current.FullName);
                });
        }
    }

    private static (int Icons, long Bytes, List<(string Alias, string Target)> Aliases) Unpack(
        Stream archive, string staging, CancellationToken token)
    {
        Directory.CreateDirectory(staging);

        var aliases = new List<(string, string)>();
        var icons = 0;
        var bytes = 0L;
        var entries = 0;

        foreach (var entry in Read(archive, token))
        {
            token.ThrowIfCancellationRequested();

            if (++entries > MaxEntries)
                throw new InvalidDataException("that archive holds more files than an icon theme should.");

            if (Contained(staging, entry.Name) is not { } path) continue;

            switch (entry.Kind)
            {
                case EntryKind.Folder:
                    Directory.CreateDirectory(path);
                    break;

                case EntryKind.Link:
                    // **Recorded, never created.** The link is what Windows
                    // refuses; the meaning of the link is all Vaktari needs,
                    // and a line of text carries it.
                    if (entry.Link is { Length: > 0 } target) aliases.Add((entry.Name, target));
                    break;

                case EntryKind.File:
                    if (!IsThemeContent(path)) break;
                    if (entry.Length > MaxEntryBytes)
                        throw new InvalidDataException($"'{entry.Name}' is far too large to be an icon.");

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                    using (var file = File.Create(path))
                    {
                        entry.CopyInto(file);
                        bytes += file.Length;
                    }

                    if (bytes > MaxTotalBytes)
                        throw new InvalidDataException("that archive unpacks to more than an icon theme should.");

                    icons++;
                    break;

                // Devices, fifos, anything else: an icon theme has none of them
                // and there is no reason to be the first thing to write one.
                default:
                    break;
            }
        }

        return (icons, bytes, aliases);
    }

    /// <summary>
    /// Moves the themes out of the staging tree, dropping whatever else the
    /// archive carried.
    ///
    /// **A theme is a folder with an index.theme in it**, found rather than
    /// assumed: a repository archive wraps everything in a name like
    /// papirus-icon-theme-master, and one download commonly holds several
    /// themes — Papirus, Papirus-Dark, Papirus-Light. Landing them side by side
    /// is also what keeps the links between them working, since those are
    /// written relative to one another.
    /// </summary>
    private static List<string> Publish(
        string staging, string destination, List<(string Alias, string Target)> aliases, CancellationToken token)
    {
        var published = new List<string>();

        foreach (var folder in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories)
                     .Where(d => File.Exists(Path.Combine(d, "index.theme")))
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();

            var name = Path.GetFileName(folder);
            if (name.Length == 0) continue;

            WriteAliases(staging, folder, aliases);

            var landing = Path.Combine(destination, name);

            // Replacing a theme already there, rather than merging into it:
            // a half-updated theme resolves to a mixture of two versions.
            Delete(landing);
            Directory.Move(folder, landing);

            published.Add(landing);
        }

        return published;
    }

    /// <summary>
    /// Writes the links that belong to one theme, as paths relative to it.
    ///
    /// Relative on both sides so the folder stays portable — a theme that is
    /// moved, or unpacked here and read from there, keeps working — and because
    /// a link into a sibling theme has to survive the pair being published
    /// together.
    /// </summary>
    private static void WriteAliases(
        string staging, string theme, List<(string Alias, string Target)> aliases)
    {
        var lines = new List<string>();

        foreach (var (alias, target) in aliases)
        {
            if (Contained(staging, alias) is not { } from) continue;
            if (!Inside(theme, from)) continue;

            // The link's target is written relative to the folder the link is
            // in, which is how tar records it and how the filesystem reads it.
            var directory = Path.GetDirectoryName(from);
            if (directory is null) continue;

            if (Contained(staging, Path.Combine(
                    Path.GetRelativePath(staging, directory), target)) is not { } to) continue;

            // Icons, and whole folders of them — a variant links a size
            // directory at a time, and a folder has no extension to recognise
            // it by, so testing content alone dropped exactly the links that
            // carry a dark theme.
            if (!Directory.Exists(to) && !IsThemeContent(to) && !IsThemeContent(from)) continue;

            lines.Add(string.Join('\t',
                Path.GetRelativePath(theme, from).Replace('\\', '/'),
                Path.GetRelativePath(theme, to).Replace('\\', '/')));
        }

        if (lines.Count > 0)
            File.WriteAllLines(Path.Combine(theme, AliasIndex), lines);
    }

    /// <summary>
    /// Where an archive entry may be written, or null if it may not be written
    /// at all.
    ///
    /// **The check every archive reader needs and many do not have.** An entry
    /// is free to call itself ..\..\Windows\System32\something, and a reader
    /// that simply combines paths will write exactly there. The rule is not
    /// that the name looks harmless but that the resolved path is genuinely
    /// underneath the destination.
    /// </summary>
    internal static string? Contained(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        if (Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal)) return null;

        string full, anchored;

        try
        {
            full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            anchored = Path.GetFullPath(root);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        return Inside(anchored, full) ? full : null;
    }

    /// <summary>
    /// Whether one path is genuinely underneath another.
    ///
    /// **Both sides normalised, because comparing a normalised path with an
    /// unnormalised one is how this went wrong.** C:/x and C:\x are the same
    /// folder and share no common prefix.
    /// </summary>
    private static bool Inside(string root, string path)
    {
        root = Path.GetFullPath(root);

        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

        return Path.GetFullPath(path).StartsWith(root, Comparison);
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// What an icon theme is made of, and nothing else.
    ///
    /// A whitelist rather than a list of things to avoid: the archive is
    /// somebody else's, and the set of file types worth refusing is not one
    /// anybody can finish writing down.
    /// </summary>
    private static bool IsThemeContent(string path)
    {
        var leaf = Path.GetFileName(path);

        if (leaf.Equals("index.theme", StringComparison.OrdinalIgnoreCase)) return true;

        var extension = Path.GetExtension(leaf);

        return extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A few bytes already read, and then the rest of the stream.
    ///
    /// The format is decided by looking at the start of the file, and neither
    /// the tar reader nor the zip reader can be asked to rewind — a download
    /// arrives as a network stream, which cannot seek at all.
    /// </summary>
    internal sealed class ConcatStream(byte[] head, Stream rest) : Stream
    {
        private int _at;
        private long _given;

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        /// <summary>
        /// **Fills the buffer, rather than returning what came easily.**
        ///
        /// A Stream is allowed to return fewer bytes than asked for and a
        /// correct reader loops; the xz decoder does not. Handing it the six
        /// sniffed bytes and nothing else made it read a real 5 MB theme as
        /// "Block check corrupt" — a checksum failure, which reads as a damaged
        /// download and is nothing of the sort. Gzip and zip tolerate short
        /// reads, so this only ever showed on the format added last.
        ///
        /// Filling here covers everything underneath as well: the buffering,
        /// and a network stream that returns whatever a packet happened to
        /// carry.
        /// </summary>
        public override int Read(Span<byte> buffer)
        {
            var total = 0;

            if (_at < head.Length)
            {
                total = Math.Min(buffer.Length, head.Length - _at);

                head.AsSpan(_at, total).CopyTo(buffer);
                _at += total;
            }

            while (total < buffer.Length)
            {
                var read = rest.Read(buffer[total..]);

                if (read <= 0) break;

                total += read;
            }

            _given += total;

            return total;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        /// <summary>
        /// **How many bytes have actually been handed out**, which is not the
        /// same as how far through the sniffed header we are — and this
        /// returned the latter.
        ///
        /// An xz file is a series of blocks each padded to a four-byte
        /// boundary, and the decoder works out that padding from where the
        /// stream says it is. Reporting a position frozen at six made it look
        /// for each block's checksum in the wrong place, and report "Block
        /// check corrupt" on a theme that was perfectly intact. Gzip and zip
        /// never ask, which is why this only appeared once xz was added, and
        /// only on a file large enough to have a block boundary at all.
        /// </summary>
        public override long Position { get => _given; set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) rest.Dispose();

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// **Stopped as it is written, not afterwards.** An archive is free to
    /// declare one byte in its header and then supply a gigabyte, so the header
    /// is a first filter and this is the one that holds.
    /// </summary>
    private static void Copy(Stream from, Stream to, string name)
    {
        var buffer = new byte[81920];
        var total = 0L;
        int read;

        while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;

            if (total > MaxEntryBytes)
                throw new InvalidDataException($"'{name}' is far too large to be an icon.");

            to.Write(buffer, 0, read);
        }
    }

    private static void Delete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Staging left behind is untidy, not broken, and throwing from a
            // cleanup would replace a real error with this one.
        }
    }
}
