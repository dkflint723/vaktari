using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Double-clicking a program.
///
/// **It opened a text editor, or it did nothing at all.** Open was one call to
/// xdg-open whatever the file was, and xdg-open never runs anything: it reads
/// the type and launches the registered handler, so a shell script — which is
/// text/x-shellscript — went to an editor, and a binary or an AppImage —
/// application/x-executable, which nothing registers against — went nowhere,
/// with no window and no message. Marking a downloaded AppImage runnable and
/// double-clicking it was indistinguishable from a click that missed, and
/// nothing else in the application could start it either: the only row that
/// could was "Run as administrator", which needs pkexec and runs it as root.
///
/// Two facts decide it and they are pinned separately, because getting either
/// one alone is a shipped bug. The mode alone would offer to RUN every file on
/// a FAT stick, where the filesystem reports 0777 for all of them. The type
/// alone would offer to run a source file somebody was reading.
/// </summary>
public sealed class RunFileTests
{
    /// <summary>What a launcher was told to start, and where from.</summary>
    private sealed record Started(string Directory, string[] Argv);

    /// <summary>
    /// A launcher whose two machine facts are ours: whether the execute bit is
    /// set, and whether a spawn works. Neither can be arranged on the agent
    /// that gates the merge, which runs Windows — File.SetUnixFileMode throws
    /// there, and starting a real program in a test starts a real program.
    /// </summary>
    private static (LinuxLauncher Launcher, List<Started> Spawns) Machine(
        bool executable = true, bool starts = true)
    {
        var spawns = new List<Started>();
        var launcher = new LinuxLauncher();

        launcher.ExecuteBitOverride = _ => executable;
        launcher.SpawnOverride = (directory, argv) =>
        {
            spawns.Add(new Started(directory, [.. argv]));
            return starts;
        };

        return (launcher, spawns);
    }

    /// <summary>The first bytes of an ELF binary. Every compiled program starts
    /// with these four, and so does every AppImage.</summary>
    private static readonly byte[] Elf = [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0];

    /// <summary>A real file with real bytes in it, cleaned up afterwards. Real
    /// rather than a seam because reading the head is the thing being pinned,
    /// and a file is the one part of this a Windows agent can produce.</summary>
    private static void WithFile(string name, byte[] content, Action<string> body)
    {
        var folder = Path.Combine(
            Path.GetTempPath(), "vaktari-run-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(folder);

        try
        {
            var file = Path.Combine(folder, name);

            File.WriteAllBytes(file, content);

            body(file);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { /* a temp folder is not worth failing over */ }
        }
    }

    private static byte[] Text(string content) => System.Text.Encoding.UTF8.GetBytes(content);

    // ---- what counts as a program -------------------------------------------

    /// <summary>
    /// The rule itself, over the bytes, with both shapes and both near misses.
    ///
    /// A shebang covers every scripting language at once — the interpreter is
    /// named on the line, so nothing here needs a table of them — and the ELF
    /// header covers every compiled binary AND every AppImage, which is an ELF
    /// runtime with a filesystem appended to it.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02 }, true)]
    [InlineData(new byte[] { (byte)'#', (byte)'!', (byte)'/' }, true)]
    // The near misses: one bit out of the ELF magic, and a comment rather than
    // a shebang. Both are ordinary file contents.
    [InlineData(new byte[] { 0x7E, 0x45, 0x4C, 0x46, 0x02 }, false)]
    [InlineData(new byte[] { (byte)'#', (byte)' ', (byte)'a' }, false)]
    // Too short to be either. An empty file with the execute bit is a thing
    // that happens — `touch` then `chmod +x` — and it is not a program.
    [InlineData(new byte[] { 0x7F }, false)]
    [InlineData(new byte[0], false)]
    public void A_program_is_an_elf_header_or_a_shebang(byte[] head, bool program)
        => Assert.Equal(program, LinuxLauncher.Program(head));

    /// <summary>
    /// And the bytes really are read off the file, with the mode beside them.
    /// An AppImage is here by name as well as by shape: it is the file this
    /// whole verb is most often wanted for, and it needs no rule of its own
    /// because it is an ELF binary.
    /// </summary>
    [Theory]
    [InlineData("Tool.AppImage", true)]
    [InlineData("install", true)]
    public void An_elf_file_on_disk_is_offered_a_run(string name, bool offered)
    {
        var (launcher, _) = Machine();

        WithFile(name, Elf, file => Assert.Equal(offered, launcher.CanRunFile(file)));
    }

    /// <summary>A script, which is the other half of the same rule and the file
    /// somebody is most likely to have just written.</summary>
    [Fact]
    public void A_script_on_disk_is_offered_a_run()
    {
        var (launcher, _) = Machine();

        WithFile("build.sh", Text("#!/bin/sh\nmake\n"),
                 file => Assert.True(launcher.CanRunFile(file)));
    }

    /// <summary>
    /// **The execute bit alone would have offered to run a holiday
    /// photograph.** A filesystem with no permission bits — a FAT stick, an
    /// exFAT card, an NTFS partition mounted beside the Linux one — reports
    /// 0777 for every file on it, so on a camera card every single row would
    /// have been a program. Both reference desktops read the type as well, and
    /// that is why this is not the mode on its own.
    /// </summary>
    [Fact]
    public void A_data_file_carrying_the_execute_bit_is_not_a_program()
    {
        var (launcher, _) = Machine();

        WithFile("holiday.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10],
                 file => Assert.False(launcher.CanRunFile(file)));
    }

    /// <summary>
    /// And the other way round: a binary nobody has marked runnable is not
    /// offered either. This is the ordinary state of an AppImage straight out
    /// of a browser — the download carries no execute bit — and running it
    /// anyway would be this application deciding something the person has not.
    /// </summary>
    [Fact]
    public void A_program_without_the_execute_bit_is_not_offered_one()
    {
        var (launcher, _) = Machine(executable: false);

        WithFile("Tool.AppImage", Elf, file => Assert.False(launcher.CanRunFile(file)));
    }

    /// <summary>
    /// **A .desktop file is never started as a program**, whatever its bits and
    /// whatever its first line. execve on one fails — it is a configuration
    /// file. Excluded from the execve route only: a double-clicked one still
    /// falls through to LinuxLauncher.Open, the desktop's own opener, which
    /// reads the Exec line and starts the application named there. The bytes
    /// here are a shebang, so the rule above would say yes without this.
    /// </summary>
    [Fact]
    public void A_desktop_entry_is_not_run_as_a_program()
    {
        var (launcher, _) = Machine();

        WithFile("Firefox.desktop", Text("#!/usr/bin/env xdg-open\n[Desktop Entry]\n"),
                 file => Assert.False(launcher.CanRunFile(file)));
    }

    /// <summary>
    /// A GUARD, and no mutation can redden it: with the existence check gone,
    /// the head read below it fails for a folder and for a path that is not
    /// there, and the answer is false either way. It is here because that check
    /// is about COST rather than correctness — CanRunSelection is read on every
    /// selection change, a folder is the likeliest selection there is, and
    /// opening one to read four bytes is a swallowed exception per click. The
    /// same reason DesktopEntries.Launcher asks before it reads a mode.
    /// </summary>
    [Fact]
    public void A_folder_and_a_missing_path_are_not_offered_a_run()
    {
        var (launcher, _) = Machine();

        Assert.False(launcher.CanRunFile(Path.GetTempPath()));
        Assert.False(launcher.CanRunFile(
            Path.Combine(Path.GetTempPath(), "vaktari-no-such-file-" + Guid.NewGuid())));
    }

    // ---- starting it ---------------------------------------------------------

    /// <summary>
    /// The program itself, started in its own folder.
    ///
    /// **The folder is the rule that matters here**, and it is the one
    /// OpenElevated already keeps: a script that reads a file sitting beside it
    /// finds it, rather than looking wherever the file manager happened to be
    /// started from — which for a desktop launch is the home directory and for
    /// a terminal launch is anywhere at all.
    /// </summary>
    [Fact]
    public void Running_a_file_starts_it_in_its_own_folder()
    {
        var (launcher, spawns) = Machine();

        var folder = Path.Combine(Path.GetTempPath(), "vendor");
        var installer = Path.Combine(folder, "install.sh");

        Assert.Null(launcher.Run(installer));

        var started = Assert.Single(spawns);

        Assert.Equal([installer], started.Argv);
        Assert.Equal(folder, started.Directory);
    }

    /// <summary>
    /// **Not wrapped in a terminal, which is where the elevated verb differs.**
    /// That one wraps pkexec because pkexec unsets DISPLAY and XAUTHORITY and
    /// what it starts has nowhere to speak; nothing is unset here, an AppImage
    /// draws its own window, and a terminal opened over one would be a window
    /// nobody asked for. Asserted against a launcher that HAS a terminal, so
    /// the argv is one because of the rule rather than for want of a candidate.
    /// </summary>
    [Fact]
    public void Running_a_file_does_not_open_a_terminal_over_it()
    {
        var (launcher, spawns) = Machine();

        launcher.UseTerminals([new TerminalOption("konsole", "Konsole", "/usr/bin/konsole", [])]);

        launcher.Run("/opt/vendor/Tool.AppImage");

        Assert.Equal(["/opt/vendor/Tool.AppImage"], Assert.Single(spawns).Argv);
    }

    /// <summary>
    /// A start that fails is handed back rather than dropped, so the status bar
    /// can say so — the same contract Open was given, and for the same reason:
    /// a program that never appears and never explains reads as a click that
    /// missed.
    /// </summary>
    [Fact]
    public void A_run_that_will_not_start_is_handed_back()
    {
        var (launcher, _) = Machine(starts: false);

        var failure = launcher.Run("/opt/vendor/install.sh");

        Assert.NotNull(failure);

        // In the words the rest of the application uses, not the exception's
        // own: this reaches Failures.Describe verbatim.
        Assert.Equal("the desktop did not start anything", failure!.Message);
    }

    /// <summary>
    /// The elevated verb reads the same execute bit through the same seam, so a
    /// change to one is a change to both. Pinned here because the elevation
    /// tests can only assert it on a real POSIX filesystem, which the agent
    /// that gates the merge is not.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void The_elevated_verb_asks_the_same_question_of_the_mode(
        bool executable, bool offered)
    {
        var (launcher, _) = Machine(executable);

        launcher.UsePkexec("/usr/bin/pkexec");

        WithFile("install", Elf, file => Assert.Equal(offered, launcher.CanElevateFile(file)));
    }
}
