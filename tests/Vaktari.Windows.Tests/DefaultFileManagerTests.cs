using System.Runtime.Versioning;
using Microsoft.Win32;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Becoming — and stopping being — the handler a double-clicked folder opens.
///
/// **Every test here runs against a scratch subtree, never the live shell
/// classes.** Verifying this against the real <c>Directory\shell</c> would
/// change the machine's actual behaviour as a side effect of running the suite,
/// and on the machine it was written on that would have silently displaced
/// another file manager the owner had deliberately chosen. The registry root is
/// a constructor argument for exactly that reason.
///
/// **What is being guarded is the restore.** Claiming the class is easy; giving
/// it back to whoever held it is where this can quietly destroy something, and
/// there is no way to notice from inside the application afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DefaultFileManagerTests : IDisposable
{
    /// <summary>Under our own key, and removed again whatever the test did.</summary>
    private const string Scratch = @"Software\Vaktari.Tests\Classes";

    private const string Exe = @"C:\Program Files\Vaktari\Vaktari.Ui.exe";

    private static WindowsDefaultFileManager Subject() => new(Exe, Scratch);

    private static void Preset(string cls, string? verb)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{Scratch}\{cls}\shell");
        if (verb is not null) key!.SetValue(null, verb);
    }

    private static string? Current(string cls)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{Scratch}\{cls}\shell");
        return key?.GetValue(null) as string;
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Vaktari.Tests", throwOnMissingSubKey: false);

        // ONLY the scoped record. This used to delete the production key, and
        // on the machine the feature was in use on that quietly destroyed the
        // note saying which handler to give the role back to.
        Registry.CurrentUser.DeleteSubKeyTree(
            $@"Software\Vaktari\DefaultFileManager\scoped_{Scratch.Replace('\\', '_')}",
            throwOnMissingSubKey: false);
    }

    /// <summary>
    /// **A scoped instance must not touch the production backup record.**
    ///
    /// This is the regression, and it was live rather than theoretical: the
    /// classes path was injectable and the backup path was a constant, so the
    /// suite's own cleanup deleted the real record on the machine the feature
    /// was in use on. Nothing failed and nothing was logged — the damage only
    /// appears when someone presses "stop being the default" and gets an
    /// unclaimed class instead of the handler they used to have.
    ///
    /// The production value is written here, the whole scoped lifecycle is run
    /// against it, and it is asserted intact afterwards.
    /// </summary>
    [Fact]
    public void A_scoped_instance_never_touches_the_real_backup()
    {
        const string Production = @"Software\Vaktari\DefaultFileManager";
        const string Sentinel = "OpenInSomethingRealAndPrecious";

        // Whatever is really there is put back afterwards. A blind delete in
        // the cleanup is the same class of mistake this test exists to catch —
        // and it made exactly that mistake first time, wiping the live record
        // it had just proved the subject would not touch.
        string? existing;
        using (var real = Registry.CurrentUser.CreateSubKey(Production))
        {
            existing = real!.GetValue("Directory") as string;
            real.SetValue("Directory", Sentinel);
        }

        try
        {
            Preset("Directory", "whatever");
            Subject().MakeDefault();
            Subject().Restore();

            using var after = Registry.CurrentUser.OpenSubKey(Production);

            Assert.Equal(Sentinel, after?.GetValue("Directory"));
        }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey(Production, writable: true);

            if (existing is null) key?.DeleteValue("Directory", throwOnMissingValue: false);
            else key?.SetValue("Directory", existing);
        }
    }

    [Fact]
    public void It_is_not_the_default_until_it_is_asked_to_be()
    {
        Assert.False(Subject().IsDefault());
    }

    [Fact]
    public void Becoming_the_default_claims_folders_AND_drives()
    {
        var result = Subject().MakeDefault();

        Assert.True(result.Succeeded, result.Message);

        // Both, because they are separate classes: a folder in a listing and a
        // drive in "This PC" are opened through different registrations, and
        // doing only the first reads as the feature half working.
        Assert.Equal("OpenInVaktari", Current("Directory"));
        Assert.Equal("OpenInVaktari", Current("Drive"));
        Assert.True(Subject().IsDefault());
    }

    [Fact]
    public void The_command_quotes_the_path_it_is_handed()
    {
        Subject().MakeDefault();

        using var key = Registry.CurrentUser.OpenSubKey(
            $@"{Scratch}\Directory\shell\OpenInVaktari\command");

        // A folder name with a space in it is the common case, not the edge
        // case: unquoted, "%1" arrives as several arguments and the path the
        // application is given is the first word of it.
        Assert.Equal($"\"{Exe}\" \"%1\"", key?.GetValue(null));
    }

    /// <summary>
    /// **A drive's is not quoted.** Its "%1" is a root, and a quoted C:\ ends
    /// the command line in \" — read as an escaped quote, so the argument
    /// arrived as C:" and double-clicking a drive opened nothing.
    /// </summary>
    [Fact]
    public void The_drive_command_leaves_the_root_unquoted()
    {
        Subject().MakeDefault();

        using var key = Registry.CurrentUser.OpenSubKey(
            $@"{Scratch}\Drive\shell\OpenInVaktari\command");

        Assert.Equal($"\"{Exe}\" %1", key?.GetValue(null));
    }

    /// <summary>
    /// The one that matters. The machine this was written on had another file
    /// manager registered; taking the role without being able to give it back
    /// would be taking something that cannot be returned.
    /// </summary>
    [Fact]
    public void Restoring_gives_the_role_back_to_whoever_had_it()
    {
        Preset("Directory", "OpenInSomethingElse");
        Preset("Drive", "OpenInSomethingElse");

        Subject().MakeDefault();
        Assert.Equal("OpenInVaktari", Current("Directory"));

        var restored = Subject().Restore();

        Assert.True(restored.Succeeded, restored.Message);
        Assert.Equal("OpenInSomethingElse", Current("Directory"));
        Assert.Equal("OpenInSomethingElse", Current("Drive"));
        Assert.False(Subject().IsDefault());
    }

    /// <summary>
    /// Pressing it twice must not record Vaktari as the thing to restore to,
    /// which would strand the real previous handler permanently.
    /// </summary>
    [Fact]
    public void Claiming_it_twice_still_remembers_the_original()
    {
        Preset("Directory", "OpenInSomethingElse");
        Preset("Drive", "OpenInSomethingElse");

        Subject().MakeDefault();
        Subject().MakeDefault();
        Subject().Restore();

        Assert.Equal("OpenInSomethingElse", Current("Directory"));
    }

    /// <summary>
    /// A machine where nothing had claimed the class: restoring must leave it
    /// unclaimed rather than inventing a handler, so the shell falls back to
    /// its own default the way it did before.
    /// </summary>
    [Fact]
    public void Restoring_an_unclaimed_class_leaves_it_unclaimed()
    {
        Subject().MakeDefault();
        Subject().Restore();

        Assert.True(string.IsNullOrEmpty(Current("Directory")));
    }

    /// <summary>
    /// And the verb key itself goes, so no stray "Open in Vaktari" entry is
    /// left on the context menu of a machine that turned the feature off.
    /// </summary>
    [Fact]
    public void Restoring_removes_the_verb_entirely()
    {
        Subject().MakeDefault();
        Subject().Restore();

        using var verb = Registry.CurrentUser.OpenSubKey(
            $@"{Scratch}\Directory\shell\OpenInVaktari");

        Assert.Null(verb);
    }
}
