namespace Vaktari.Core.FileSystem;

/// <summary>
/// Takes files that a drop is offering but which are not on disk.
///
/// **This is what dragging out of an archive is.** 7-Zip and Explorer's own zip
/// view both hand over a list of names and a stream for each, rather than
/// paths — the files do not exist anywhere until somebody asks for their
/// contents. A drop handler that looks only for paths therefore sees nothing,
/// which is why the drag appeared to do nothing at all.
///
/// Null where the desktop has no such notion, which is every desktop but
/// Windows: the freedesktop world passes URIs, and a file that has no location
/// has no URI either.
/// </summary>
public interface IVirtualFileDrop
{
    /// <summary>
    /// Whether this drop is offering files with no location on disk — and
    /// ONLY such files: Explorer describes an ordinary file on disk with the
    /// same descriptor it uses for a zip's contents, beside its path, and a
    /// drag that carries paths is a drag of those paths.
    ///
    /// Asked while the pointer is still moving, so it must be cheap: it reads
    /// the list of formats and nothing else.
    /// </summary>
    bool Offers(object dataTransfer);

    /// <summary>
    /// <see cref="Offers(object)"/>, and when the answer is no because the
    /// question could not be put, why.
    ///
    /// **A failed question and a plain no were the same false.** The drag
    /// source lives in another process, so asking it anything can fail for a
    /// moment — and a drag whose last answer is no is taken away by Windows
    /// without ever being dropped. That drag vanished with nothing, anywhere,
    /// saying why; <paramref name="failure"/> is what lets the window say so.
    /// Null for an honest no.
    /// </summary>
    bool Offers(object dataTransfer, out string? failure);

    /// <summary>
    /// Writes them somewhere real and returns the paths, or an empty list if
    /// nothing could be taken.
    ///
    /// **Called once, on the drop.** Extracting on every pointer move would
    /// unpack an archive to disk for a drag that never landed.
    ///
    /// The caller owns what comes back and is expected to MOVE it into place:
    /// there is no original to preserve, so a copy would leave a duplicate in
    /// the temporary folder for nobody.
    /// </summary>
    IReadOnlyList<string> Take(object dataTransfer, CancellationToken token = default);

    /// <summary>
    /// Tells the drop's source, on the data object itself, that the target
    /// has moved the files and the source must not delete them — the shell's
    /// "optimized move". False when the source could not be told, which costs
    /// nothing: the drop's own effect already says the same.
    ///
    /// On this interface because it is the one place the native data object
    /// behind a drop can be reached; it has nothing to do with archive files
    /// beyond that.
    /// </summary>
    bool MovedByTarget(object dataTransfer);
}
