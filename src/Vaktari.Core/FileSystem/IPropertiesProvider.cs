namespace Vaktari.Core.FileSystem;

/// <summary>One labelled fact about a file. Platform extras arrive as these
/// rather than as typed fields, so Core does not grow a permissions model that
/// only means something on one operating system.</summary>
public sealed record PropertyRow(string Label, string Value);

public sealed record PropertyGroup(string Label, IReadOnlyList<PropertyRow> Rows);

/// <summary>
/// Everything a properties view shows. The universal fields are typed because
/// every platform has them; anything platform-specific — POSIX permissions,
/// NTFS ACLs, alternate data streams — lives in <see cref="Groups"/>.
/// </summary>
public sealed record FileDetails
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }

    public string Kind { get; init; } = "";
    public long Size { get; init; }

    public DateTimeOffset? Modified { get; init; }
    public DateTimeOffset? Accessed { get; init; }
    public DateTimeOffset? Created { get; init; }

    public string? SymlinkTarget { get; init; }

    public IReadOnlyList<PropertyGroup> Groups { get; init; } = [];
}

/// <summary>Progress while a directory is being measured.</summary>
public readonly record struct SizeProgress(long Bytes, int Files, int Folders);

public interface IPropertiesProvider
{
    ValueTask<FileDetails> GetAsync(string path, CancellationToken ct);

    /// <summary>
    /// Walks a directory to total its contents. Explicitly on demand: doing it
    /// automatically would make opening properties on a home directory hang.
    /// </summary>
    ValueTask<SizeProgress> MeasureAsync(
        string path, IProgress<SizeProgress> progress, CancellationToken ct);

    /// <summary>
    /// Shows the DESKTOP's own properties dialog, where the desktop has one
    /// worth deferring to, and reports whether it did.
    ///
    /// **Defaulted to "no", so a platform that has no such dialog says nothing
    /// and keeps Vaktari's own window.** Windows overrides it: its sheet
    /// carries Security, Details and the Unblock checkbox, none of which a
    /// hand-written window can offer, and those are the reasons anyone opens
    /// properties there.
    /// </summary>
    bool ShowSystemDialog(string path) => false;

    /// <summary>
    /// What a platform can say about a WHOLE selection, for the window that
    /// opens when more than one row is asked about.
    ///
    /// **A selection's window had nothing below the size line.** One item fills
    /// that half from <see cref="FileDetails.Groups"/> — the attribute set on
    /// Windows, the mode and the owner on Linux — and a selection asked for no
    /// such thing, so a person who selected twenty files got a count, a total,
    /// and then empty space. On Windows that is the whole of the answer: the
    /// shell's own sheet is declined for more than one path.
    ///
    /// Deliberately not <see cref="GetAsync"/> in a loop, which is the obvious
    /// way to fill this. That call is per-item expensive on purpose — the Linux
    /// provider spawns `stat` for the owner on every path, and `xdg-mime` on
    /// top of that for every file its in-process glob table cannot name — so a
    /// selection of twenty is twenty processes at least and a thousand is a
    /// thousand. A platform answers here only with what it can read cheaply
    /// about every path, and one with nothing cheap to say stays silent and
    /// leaves the window exactly as it was.
    /// </summary>
    ValueTask<IReadOnlyList<PropertyGroup>> GetSharedAsync(
        IReadOnlyList<string> paths, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<PropertyGroup>>([]);
}
