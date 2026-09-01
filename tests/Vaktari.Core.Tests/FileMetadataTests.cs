using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What a copy has to carry besides the bytes.
///
/// Both engines copied through a bare stream loop, so every copy landed with
/// today's date and default permissions: a copied script lost its executable
/// bit, a 0600 key came out world-readable, and copying a photo library
/// re-dated every file in it — a loss nobody notices until the sort order is
/// wrong months later.
/// </summary>
public sealed class FileMetadataTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("vaktari-metadata").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Make(string name, string content = "hello")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// The one everybody notices eventually: a copied file that claims to have
    /// been written today.
    /// </summary>
    [Fact]
    public void A_copy_keeps_the_time_it_was_last_written()
    {
        var source = Make("original.txt");
        var when = new DateTime(2019, 4, 17, 9, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, when);

        var target = Make("copy.txt");

        FileMetadata.Carry(source, target);

        // Filesystem timestamp resolution varies; seconds is the honest
        // granularity to assert across FAT, ext4 and NTFS.
        Assert.Equal(when, File.GetLastWriteTimeUtc(target), TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// **The executable bit, on the platform where it decides whether a file
    /// runs at all.** A copied shell script that will not execute is the loss
    /// people actually hit.
    /// </summary>
    [Fact]
    public void A_copied_script_on_linux_can_still_be_run()
    {
        if (OperatingSystem.IsWindows()) return;

        var source = Make("run.sh", "#!/bin/sh\necho hi\n");
        var target = Make("run-copy.sh", "#!/bin/sh\necho hi\n");

        File.SetUnixFileMode(source,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        FileMetadata.Carry(source, target);

        Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
    }

    /// <summary>
    /// The other direction, and the one that is a security problem rather than
    /// an annoyance: a private key copied out to be readable by everyone.
    /// </summary>
    [Fact]
    public void A_copied_private_key_on_linux_stays_private()
    {
        if (OperatingSystem.IsWindows()) return;

        var source = Make("id_ed25519", "secret");
        var target = Make("id_ed25519.bak", "secret");

        File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(target,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        FileMetadata.Carry(source, target);

        var mode = File.GetUnixFileMode(target);

        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
    }

    [Fact]
    public void A_copy_keeps_the_read_only_and_hidden_marks_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = Make("marked.txt");
        var target = Make("marked-copy.txt");

        File.SetAttributes(source, FileAttributes.ReadOnly | FileAttributes.Hidden);

        try
        {
            FileMetadata.Carry(source, target);

            var attributes = File.GetAttributes(target);

            Assert.True(attributes.HasFlag(FileAttributes.ReadOnly));
            Assert.True(attributes.HasFlag(FileAttributes.Hidden));
        }
        finally
        {
            // Or the temp folder cannot be cleaned up.
            File.SetAttributes(source, FileAttributes.Normal);
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// **Times before attributes.** A file made read-only first refuses its own
    /// timestamps, so the wrong order silently drops the dates on exactly the
    /// archival files most likely to be marked read-only.
    /// </summary>
    [Fact]
    public void A_read_only_file_still_gets_its_timestamp()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = Make("old.txt");
        var target = Make("old-copy.txt");

        var when = new DateTime(2011, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, when);
        File.SetAttributes(source, FileAttributes.ReadOnly);

        try
        {
            FileMetadata.Carry(source, target);

            Assert.Equal(when, File.GetLastWriteTimeUtc(target), TimeSpan.FromSeconds(2));
            Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            File.SetAttributes(source, FileAttributes.Normal);
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// **Never throws.** A copy that landed correctly must not be reported as
    /// failed because the filesystem underneath would not take a timestamp —
    /// FAT, a phone over MTP and most network shares all refuse something.
    /// </summary>
    [Fact]
    public void A_target_that_is_not_there_is_not_an_error()
    {
        var source = Make("present.txt");

        FileMetadata.Carry(source, Path.Combine(_root, "nowhere", "gone.txt"));
        FileMetadata.Carry(Path.Combine(_root, "missing.txt"), source);
    }

    /// <summary>
    /// Directory and reparse-point flags describe what a file IS, not how it is
    /// marked. Carrying them onto a plain copy either throws or lies about it.
    /// </summary>
    [Fact]
    public void The_flags_that_describe_what_a_file_is_are_not_carried()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = Make("plain.txt");
        var target = Make("plain-copy.txt");

        FileMetadata.Carry(source, target);

        var attributes = File.GetAttributes(target);

        Assert.False(attributes.HasFlag(FileAttributes.Directory));
        Assert.False(attributes.HasFlag(FileAttributes.ReparsePoint));
    }
}
