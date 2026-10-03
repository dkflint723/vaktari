namespace Vaktari.Core.FileSystem;

/// <summary>
/// Something has <see cref="Path"/>, or something inside it, open, and the
/// file system would not let go of it.
///
/// **A folder held from below said "Access to the path … is denied."** Windows
/// answers ERROR_ACCESS_DENIED (0x80070005) when a folder is renamed or moved
/// whole while anything beneath it is open — a file inside, a program whose
/// current folder is a subfolder, an Explorer window or a watcher on one — and
/// .NET hands that over as a plain IOException, so Failures.Describe passed
/// its message straight through. Measured on Windows 11 (rename-notes, probes
/// 1–5): the same code for a held subfolder as for a real access-control
/// denial, which is why the engine asks the file system two more questions
/// before it throws this (see the Windows InUseCheck).
///
/// **It says THAT something has it open, never which program.** Vaktari does
/// not look inside other programs; Resource Monitor and PowerToys File
/// Locksmith can (README, Known limits).
///
/// **An IOException, with the HResult it arrived with**, so every catch that
/// already handles a sharing violation still handles this one.
/// </summary>
public sealed class InUseException : IOException
{
    public InUseException(string path, bool isDirectory, int hresult, bool itselfOpen = false)
        : base(Sentence(isDirectory, itselfOpen), hresult)
    {
        Path = path;
        IsDirectory = isDirectory;
        ItselfOpen = itselfOpen;
    }

    /// <summary>What could not be renamed, moved or binned.</summary>
    public string Path { get; }

    public bool IsDirectory { get; }

    /// <summary>
    /// Whether the folder ITSELF is open — somebody's current folder, or a
    /// handle on it without delete sharing (ERROR_SHARING_VIOLATION) — rather
    /// than something beneath it. Only meaningful for a folder.
    /// </summary>
    public bool ItselfOpen { get; }

    /// <summary>
    /// The words <see cref="Failures.Describe"/> uses, kept here so the
    /// message and the sentence cannot disagree.
    ///
    /// **"Something inside", not "a file inside"** (rename QA). A folder held
    /// from below answers the same whether a file in it is open, a terminal is
    /// working in a subfolder, or a window or a watcher is on one, and the
    /// engine cannot tell those apart without looking inside other programs,
    /// which Vaktari does not do. So the sentence claims only what is known.
    /// </summary>
    public static string Sentence(bool isDirectory, bool itselfOpen)
        => !isDirectory ? "something else has that file open"
            : itselfOpen ? "something has that folder open"
            : "something inside that folder is open";
}
