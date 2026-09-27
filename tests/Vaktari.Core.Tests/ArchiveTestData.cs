using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using Vaktari.Core.FileSystem;

namespace Vaktari.Core.Tests;

/// <summary>
/// Archives for the extraction tests: the committed fixtures (M and V in
/// tests/Fixtures/Archives/PROVENANCE.md), and G archives generated here at
/// run time — zips through the BCL, tars through System.Formats.Tar, 7z
/// through SharpCompress's writer, and single streams through each
/// compressor's own Compress mode (E-30: bzip2, lzip and zstd all round-trip).
/// </summary>
internal static class ArchiveTestData
{
    internal static string FixturesDir => Path.Combine(RepoSource.Root, "tests", "Fixtures", "Archives");

    internal static string Fixture(string name) => Path.Combine(FixturesDir, name);

    /// <summary>A zip holding exactly these entries, in this order — names
    /// ending in <c>/</c> are folders.</summary>
    internal static string Zip(string path, params (string Entry, string Content)[] entries)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        foreach (var (entry, content) in entries)
        {
            var e = zip.CreateEntry(entry);

            if (entry.EndsWith('/')) continue;

            using var writer = new StreamWriter(e.Open());
            writer.Write(content);
        }

        return path;
    }

    /// <summary>A zip whose every entry carries <paramref name="when"/>.</summary>
    internal static string DatedZip(string path, DateTime when, params (string Entry, string Content)[] entries)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        foreach (var (entry, content) in entries)
        {
            var e = zip.CreateEntry(entry);
            e.LastWriteTime = when;

            if (entry.EndsWith('/')) continue;

            using var writer = new StreamWriter(e.Open());
            writer.Write(content);
        }

        return path;
    }

    internal static string Tar(string path, Action<TarWriter> fill, Func<Stream, Stream>? compress = null)
    {
        using var file = File.Create(path);
        using var outer = compress?.Invoke(file) ?? file;
        using var tar = new TarWriter(outer, TarEntryFormat.Pax, leaveOpen: true);

        fill(tar);

        return path;
    }

    internal static PaxTarEntry File_(string name, string content, UnixFileMode? mode = null)
    {
        var e = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        };

        if (mode is { } m) e.Mode = m;

        return e;
    }

    internal static PaxTarEntry Folder_(string name, UnixFileMode? mode = null)
    {
        var e = new PaxTarEntry(TarEntryType.Directory, name);

        if (mode is { } m) e.Mode = m;

        return e;
    }

    internal static PaxTarEntry Link_(TarEntryType type, string name, string target)
        => new(type, name) { LinkName = target };

    /// <summary>One compressed stream holding <paramref name="data"/>.</summary>
    internal static string Bare(string path, ArchiveFormat format, byte[] data)
    {
        using (var file = File.Create(path))
        using (var stream = Compressor(format)(file))
            stream.Write(data);

        return path;
    }

    internal static Func<Stream, Stream> Compressor(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Gz or ArchiveFormat.TarGz => s => new GZipStream(s, CompressionLevel.Optimal, leaveOpen: true),
        ArchiveFormat.Bz2 or ArchiveFormat.TarBz2 => s => SharpCompress.Compressors.BZip2.BZip2Stream.Create(
            s, SharpCompress.Compressors.CompressionMode.Compress, false, leaveOpen: true),
        ArchiveFormat.Zst or ArchiveFormat.TarZst => s => new SharpCompress.Compressors.ZStandard.CompressionStream(s, leaveOpen: true),
        ArchiveFormat.Lz or ArchiveFormat.TarLz => s => SharpCompress.Compressors.LZMA.LZipStream.Create(
            s, SharpCompress.Compressors.CompressionMode.Compress, leaveOpen: true),
        _ => throw new NotSupportedException($"no compressor for {format}"),
    };

    /// <summary>A 7z, one LZMA2 stream per entry, through SharpCompress's
    /// writer.</summary>
    internal static string SevenZip(string path, params (string Entry, byte[] Content)[] entries)
    {
        using var file = File.Create(path);
        using var writer = new SevenZipWriter(file, new SevenZipWriterOptions(CompressionType.LZMA2));

        foreach (var (entry, content) in entries)
            writer.Write(entry, new MemoryStream(content), DateTime.Now);

        return path;
    }
}
