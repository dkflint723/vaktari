using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The three bits the permissions dialog does not offer.
///
/// **Applying any change cleared them.** The mode is assembled from the nine
/// rwx toggles alone, so ticking "group can write" on a setuid binary stopped
/// it being one, and doing the same to a shared directory lost its sticky bit —
/// the one that stops people deleting each other's files in it. Neither is a
/// change anybody asked for, and neither said a word when it happened.
///
/// **And they appeared nowhere.** ls has shown them in the execute column for
/// fifty years; a permissions row that omits them says a setuid binary is an
/// ordinary one.
/// </summary>
public sealed class SpecialBitTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-special-" + Guid.NewGuid().ToString("N")[..8]);

    public SpecialBitTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string File_(string name, UnixFileMode mode)
    {
        var path = Path.Combine(_root, name);

        System.IO.File.WriteAllText(path, name);
        System.IO.File.SetUnixFileMode(path, mode);

        return path;
    }

    /// <summary>The provider's own keys, and a change that touches several of
    /// the nine so the apply is a real one.</summary>
    private static readonly AccessToggle[] ReadWriteForEveryone =
    [
        new("ur", "owner",  "read",    true),
        new("uw", "owner",  "write",   true),
        new("ux", "owner",  "execute", true),
        new("gr", "group",  "read",    true),
        new("gw", "group",  "write",   true),
        new("gx", "group",  "execute", false),
        new("or", "others", "read",    true),
        new("ow", "others", "write",   false),
        new("ox", "others", "execute", false),
    ];

    private static async Task ApplyAsync(string path)
        => await new LinuxPropertiesProvider().SetAccessAsync(
            path, ReadWriteForEveryone, recursive: false, progress: null, CancellationToken.None);

    /// <summary>
    /// The whole finding: a setuid binary is still one after somebody changes
    /// who may write to it.
    /// </summary>
    [PosixFact]
    public async Task Changing_who_can_write_leaves_a_setuid_file_setuid()
    {
        var path = File_("tool", UnixFileMode.UserRead | UnixFileMode.UserExecute
                                 | UnixFileMode.SetUser);

        await ApplyAsync(path);

        Assert.True(System.IO.File.GetUnixFileMode(path).HasFlag(UnixFileMode.SetUser));
    }

    /// <summary>
    /// And a shared directory keeps the bit that stops people deleting each
    /// other's files in it.
    /// </summary>
    [PosixFact]
    public async Task And_a_shared_folder_keeps_its_sticky_bit()
    {
        var path = Path.Combine(_root, "shared");

        Directory.CreateDirectory(path);
        System.IO.File.SetUnixFileMode(
            path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                  | UnixFileMode.StickyBit);

        await ApplyAsync(path);

        Assert.True(System.IO.File.GetUnixFileMode(path).HasFlag(UnixFileMode.StickyBit));
    }

    /// <summary>
    /// **And a file that never had them does not gain them.** Preserving is not
    /// the same as setting, and reading the bits once from the folder and
    /// carrying them down would put setuid on every file in a tree — which is
    /// worse than clearing it.
    /// </summary>
    [PosixFact]
    public async Task A_file_without_them_does_not_gain_them()
    {
        var setuid = File_("tool", UnixFileMode.UserRead | UnixFileMode.SetUser);
        var plain = File_("notes.txt", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await ApplyAsync(setuid);
        await ApplyAsync(plain);

        Assert.False(System.IO.File.GetUnixFileMode(plain).HasFlag(UnixFileMode.SetUser));
    }

    /// <summary>
    /// The nine toggles still do what they say — preserving the other three
    /// must not become "change nothing".
    /// </summary>
    [PosixFact]
    public async Task The_ordinary_bits_still_change()
    {
        var path = File_("notes.txt", UnixFileMode.UserRead);

        await ApplyAsync(path);

        var mode = System.IO.File.GetUnixFileMode(path);

        Assert.True(mode.HasFlag(UnixFileMode.GroupWrite));
        Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
    }

    // ---- and they are shown --------------------------------------------------
    //
    // Read straight off the mode rather than off a file, so every combination
    // can be asked for — including ones no ordinary machine has — and so this
    // half runs on the Windows agents too, where there are no unix modes to
    // build a file with.

    private static string Symbolic(UnixFileMode mode)
        => LinuxPropertiesProvider.Symbolic(mode);

    /// <summary>An ordinary file reads exactly as it always did.</summary>
    [Fact]
    public void An_ordinary_file_is_unchanged()
        => Assert.Equal(
            "rw-r--r--",
            Symbolic(UnixFileMode.UserRead | UnixFileMode.UserWrite
                     | UnixFileMode.GroupRead | UnixFileMode.OtherRead));

    /// <summary>
    /// ls writes a lowercase s where the execute bit beneath it is on, in the
    /// column the bit belongs to — setuid in the owner's, setgid in the
    /// group's.
    /// </summary>
    [Theory]
    [InlineData(UnixFileMode.SetUser, UnixFileMode.UserExecute, "rws------")]
    [InlineData(UnixFileMode.SetGroup, UnixFileMode.GroupExecute, "rw---s---")]
    public void A_special_bit_over_an_execute_bit_is_a_small_letter(
        UnixFileMode special, UnixFileMode execute, string expected)
        => Assert.Equal(
            expected,
            Symbolic(UnixFileMode.UserRead | UnixFileMode.UserWrite | special | execute));

    /// <summary>
    /// **And a CAPITAL where it is off**, which is not decoration: "setuid, and
    /// executable" and "setuid, and not" are different situations, and one
    /// letter for both would hide the second — a file carrying the bit that
    /// cannot use it.
    /// </summary>
    [Fact]
    public void A_special_bit_with_nothing_to_execute_is_a_capital()
        => Assert.Equal(
            "r-S------",
            Symbolic(UnixFileMode.UserRead | UnixFileMode.SetUser));

    /// <summary>The sticky bit is a t, in the last column.</summary>
    [Fact]
    public void A_sticky_folder_ends_in_a_t()
        => Assert.EndsWith(
            "t",
            Symbolic(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                     | UnixFileMode.OtherExecute | UnixFileMode.StickyBit));

    /// <summary>And a capital T when nothing may enter it.</summary>
    [Fact]
    public void And_a_capital_t_when_nothing_may_enter_it()
        => Assert.EndsWith("T", Symbolic(UnixFileMode.UserRead | UnixFileMode.StickyBit));

    /// <summary>Nothing set at all is nine dashes.</summary>
    [Fact]
    public void Nothing_at_all_is_nine_dashes()
        => Assert.Equal("---------", Symbolic(UnixFileMode.None));
}
