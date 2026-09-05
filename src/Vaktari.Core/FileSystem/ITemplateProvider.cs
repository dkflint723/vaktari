namespace Vaktari.Core.FileSystem;

/// <param name="Name">What the menu says.</param>
/// <param name="Path">
/// Where the new file's bytes come from: the file on disk to copy, unless
/// <see cref="FileTemplate.Content"/> carries them instead, in which case there
/// is no file and this is only a leaf to name the new one after.
/// </param>
public sealed record FileTemplate(string Name, string Path)
{
    /// <summary>
    /// The bytes to write, or null when the template really is a file to copy.
    ///
    /// **The one row Windows itself ships under New cannot be written as a
    /// path.** Measured on Windows 11 26200, all 24 ShellNew keys under
    /// HKEY_CLASSES_ROOT: the two for <c>.zip</c> carry a 22-byte <c>Data</c>
    /// blob — the end-of-central-directory record of an empty archive — and no
    /// file on the machine holds those bytes. Five keys name a seed file (Word,
    /// Excel, PowerPoint, Publisher, Access), two name a Handler, two a
    /// Command, and thirteen carry no value at all. A provider that could only
    /// hand back paths had to drop .zip, which is one reason
    /// <c>WindowsTemplates</c> read an empty folder instead of the registry for
    /// a release. The <c>NullFile</c> directive — "make an empty one" — needs
    /// the same treatment and is the shape a clean machine has most of.
    ///
    /// Linux never sets this: <c>XDG_TEMPLATES_DIR</c> holds real files.
    /// </summary>
    public byte[]? Content { get; init; }

    /// <summary>
    /// What to call the new file, or null to call it whatever
    /// <see cref="Path"/> is called.
    ///
    /// **A seed file is named by whoever installed it, not by the menu row.**
    /// Measured: <c>HKCR\.accdb\Access.Application.16\ShellNew</c> names
    /// <c>…\Office16\1033\ACCESS12.ACC</c>, so a copy that kept the seed's own
    /// leaf made "New &gt; Microsoft Access Database" produce <c>ACCESS12.ACC</c>
    /// — not the row's name, and not even the <c>.accdb</c> the row is the row
    /// for. The other four seeded rows here were the same shape: word.docx,
    /// excel12.xlsx, powerpoint.pptx, mspub.pub. Explorer makes
    /// "New Microsoft Access Database.accdb" from that key.
    ///
    /// Linux never sets this: an XDG template is a file the user named
    /// themselves, so the seed's leaf is the answer.
    /// </summary>
    public string? Leaf { get; init; }
}

/// <summary>
/// The "new file from template" list. Platform-specific because the location is
/// a desktop convention — <c>XDG_TEMPLATES_DIR</c> here, the Windows
/// <c>ShellNew</c> registry keys there — even though using one is just a copy.
/// </summary>
public interface ITemplateProvider
{
    IReadOnlyList<FileTemplate> Discover();
}
