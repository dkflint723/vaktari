using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// **The spelling a READ goes through.** <see cref="ReachablePath.Refuse"/>
/// stops an act on a name Win32 folds; a read can simply reach the right file,
/// through the one spelling Win32 opens as written, a literal "\\?\". Exact is
/// that spelling where the plain one folds and the plain one where it does
/// not; Extended is it always, for a walk whose children must keep their
/// names. Where there is no such spelling — a device path Win32 rewrites, a
/// "." or ".." between names that "\\?\" would take literally, a relative
/// path, a "/" — neither guesses: null, and the caller shows nothing.
/// </summary>
public sealed class TrailingNameSpellingTests
{
    [WindowsTheory]
    [InlineData(@"C:\work\report ", @"\\?\C:\work\report ")]
    [InlineData(@"C:\work\report.", @"\\?\C:\work\report.")]
    [InlineData(@"C:\work\album \notes.txt", @"\\?\C:\work\album \notes.txt")]
    [InlineData(@"\\server\share\report ", @"\\?\UNC\server\share\report ")]
    public void A_plain_path_that_folds_is_read_through_the_extended_prefix(string path, string exact)
        => Assert.Equal(exact, ReachablePath.Exact(path));

    [WindowsTheory]
    [InlineData(@"C:\work\report")]
    [InlineData(@"C:\work\a.b")]
    [InlineData(@"C:\")]
    [InlineData(@"\\?\C:\work\report ")]
    [InlineData(@"\??\C:\work\report ")]
    [InlineData(@"C:\work\report \..")]
    [InlineData(@"relative\report")]
    public void A_path_that_opens_as_written_is_read_as_written(string path)
        => Assert.Equal(path, ReachablePath.Exact(path));

    [WindowsTheory]
    [InlineData(@"\\.\C:\work\report ")]
    [InlineData(@"//?/C:/work/report ")]
    [InlineData(@"C:/work/report ")]
    [InlineData(@"relative\report ")]
    [InlineData(@"C:\work\.\report ")]
    [InlineData(@"C:\work\\report ")]
    [InlineData("C:\\work\\re\0port ")]
    public void A_folded_path_with_no_exact_spelling_is_read_nowhere(string path)
        => Assert.Null(ReachablePath.Exact(path));

    [WindowsTheory]
    [InlineData(@"C:\work\album", @"\\?\C:\work\album")]
    [InlineData(@"C:\", @"\\?\C:\")]
    [InlineData(@"\\server\share\album", @"\\?\UNC\server\share\album")]
    [InlineData(@"\\?\C:\work\album", @"\\?\C:\work\album")]
    public void A_walk_root_is_always_spelled_so_its_children_keep_their_names(string path, string extended)
        => Assert.Equal(extended, ReachablePath.Extended(path));

    [WindowsTheory]
    [InlineData(@"\\.\C:\work")]
    [InlineData(@"C:\work\..\album")]
    [InlineData(@"work\album")]
    public void A_walk_root_with_no_extended_spelling_has_none(string path)
        => Assert.Null(ReachablePath.Extended(path));
}
