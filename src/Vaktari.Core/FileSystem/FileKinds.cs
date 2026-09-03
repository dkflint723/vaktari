namespace Vaktari.Core.FileSystem;

/// <summary>A kind of empty file the user can create directly.</summary>
/// <param name="Label">What the menu says.</param>
/// <param name="Extension">Including the dot; empty for a file with none.</param>
/// <param name="Executable">Set the executable bit on creation.</param>
public sealed record NewFileKind(string Label, string Extension, bool Executable = false);

/// <summary>
/// The built-in "new file" list.
///
/// Exists because <see cref="ITemplateProvider"/> only ever offers what is in
/// the user's XDG Templates folder, which is empty on a stock system — so the
/// menu was there and did nothing for anyone who had not populated it. These
/// need no files on disk and work on a fresh install.
///
/// **Deliberately short.** A menu of thirty extensions is a worse answer than a
/// menu of eight: the long tail is better served by creating a text file and
/// renaming it, which costs one keystroke more and needs no list at all. These
/// are the ones worth a click.
/// </summary>
public static class FileKinds
{
    /// <summary>
    /// The list one platform offers.
    ///
    /// **The menu offered a shell script on Windows.** These were written on
    /// Linux and never asked which machine they were on, so the Windows build
    /// offered to make a .sh: nothing on a stock Windows runs one, and the
    /// executable bit that is the whole point of the entry is skipped there by
    /// the create path itself. The slot is worth keeping — "a script I can
    /// run" is why it is on the list — so what fills it is the thing the
    /// platform actually executes. .cmd rather than .ps1: cmd.exe runs it as
    /// it stands, where a PowerShell script meets the execution policy first,
    /// and an empty file that refuses to run is the failure this list exists
    /// to avoid.
    ///
    /// **Python stays on both.** It is not a Unix file type — a .py on Windows
    /// is associated with the launcher the installer puts there — so dropping
    /// it would take away something that works.
    ///
    /// Chosen by argument rather than by asking the operating system, so both
    /// answers can be pinned by a test running on either machine.
    /// </summary>
    public static IReadOnlyList<NewFileKind> For(bool windows) =>
    [
        new("Text file", ".txt"),
        new("Markdown document", ".md"),

        windows
            ? new NewFileKind("Batch file", ".cmd")
            : new NewFileKind("Shell script", ".sh", Executable: true),

        // Windows has no executable bit to set, and the create path refuses to
        // try — so the flag says false there rather than being quietly ignored.
        new("Python script", ".py", Executable: !windows),

        new("JSON file", ".json"),
        new("CSV spreadsheet", ".csv"),
        new("HTML page", ".html"),

        // Last, and with no extension: the escape hatch for everything the list
        // above does not cover.
        new("Empty file", ""),
    ];

    /// <summary>The list for the machine this is running on.</summary>
    public static IReadOnlyList<NewFileKind> Common { get; } = For(OperatingSystem.IsWindows());
}
