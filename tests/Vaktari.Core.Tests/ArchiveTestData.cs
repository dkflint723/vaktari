using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Vaktari.Core.FileSystem;

namespace Vaktari.Core.Tests;

/// <summary>
/// Archives for the extraction tests: the committed fixtures (M and V in
/// tests/Fixtures/Archives/PROVENANCE.md), and G archives generated here at
/// run time — zips through the BCL, tars through System.Formats.Tar, and
/// gzip streams through the runtime's GZipStream.
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
        _ => throw new NotSupportedException($"no compressor for {format}"),
    };
}
