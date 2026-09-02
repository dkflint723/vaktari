using Vaktari.Ui.Input;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A rename the system will not accept.
///
/// **The editor closed first and reported afterwards.** Typing a colon, or CON,
/// or "..", tore the rename bar down, sent the name to the file system layer,
/// and surfaced the refusal as a status line a moment later — by which time the
/// box holding the typed name was gone, so correcting one character meant
/// pressing F2 and typing the whole name again.
///
/// **And a name of nothing but spaces disappeared without a word.** Clean
/// reduced it to empty, the switch arm's length guard failed, no case matched
/// at all, and the rename was simply dropped. Nothing said why, because nothing
/// had noticed.
///
/// <c>FileNames.Refuse</c> was written for exactly this and had no caller
/// anywhere in the interface.
/// </summary>
public sealed class RefusedRenameTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    public void A_name_that_cannot_be_used_is_refused_with_a_reason(string typed)
    {
        var decision = RenamePrompt.Decide(typed, "notes.txt");

        Assert.Equal(RenameVerdict.Refused, decision.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason),
                     "refused without saying why, which is what it did before");
    }

    /// <summary>
    /// Separate from the theory above because the rules are deliberately
    /// platform-conditional: ext4 accepts a colon, and asserting otherwise on
    /// Linux would be asserting a bug.
    /// </summary>
    [Fact]
    public void A_windows_refusal_is_still_a_refusal()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(RenameVerdict.Refused, RenamePrompt.Decide("notes:stream", "notes.txt").Verdict);
        Assert.Equal(RenameVerdict.Refused, RenamePrompt.Decide("CON", "notes.txt").Verdict);
        Assert.Equal(RenameVerdict.Refused, RenamePrompt.Decide("a?b.txt", "notes.txt").Verdict);

        // Not a false positive on a name that merely begins with a device word.
        Assert.Equal(RenameVerdict.Rename, RenamePrompt.Decide("CONTENTS.txt", "notes.txt").Verdict);
    }

    [Fact]
    public void A_good_name_arrives_tidied()
    {
        var decision = RenamePrompt.Decide("  notes2.txt  ", "notes.txt");

        Assert.Equal(RenameVerdict.Rename, decision.Verdict);
        Assert.Equal("notes2.txt", decision.Name);
    }

    /// <summary>Nothing to do, and nothing to complain about either — this is
    /// what pressing Enter without typing means.</summary>
    [Fact]
    public void The_same_name_is_not_a_rename()
    {
        Assert.Equal(RenameVerdict.Unchanged, RenamePrompt.Decide("notes.txt", "notes.txt").Verdict);
        Assert.Equal(RenameVerdict.Unchanged, RenamePrompt.Decide("notes.txt ", "notes.txt").Verdict);
    }

    /// <summary>
    /// Guards the ordinal comparison. Treating these as the same name would
    /// swallow the exact correction the Windows backend goes out of its way to
    /// let through.
    /// </summary>
    [Fact]
    public void Fixing_only_the_case_is_still_a_rename()
        => Assert.Equal(RenameVerdict.Rename, RenamePrompt.Decide("README.txt", "readme.txt").Verdict);

    [Fact]
    public void The_hint_carries_the_reason_while_you_type()
    {
        var refused = RenamePrompt.HintFor("   ", "notes.txt");

        Assert.NotEqual(RenamePrompt.Hint, refused);
        Assert.Contains("esc to cancel", refused);

        // And goes back to the ordinary line once the name is usable, rather
        // than leaving a stale complaint under a name that is now fine.
        Assert.Equal(RenamePrompt.Hint, RenamePrompt.HintFor("notes2.txt", "notes.txt"));
    }

    /// <summary>
    /// The half that cannot be seen from the seam: the window has to ask before
    /// it closes the bar. Asking afterwards is exactly the bug, and every test
    /// above would still pass.
    /// </summary>
    [Fact]
    public void The_window_decides_before_it_closes_the_bar()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        var source = File.ReadAllText(
            Path.Combine(here!, "src", "Vaktari.Ui", "MainWindow.axaml.cs"));

        var at = source.IndexOf("private void ConfirmPrompt()", StringComparison.Ordinal);
        Assert.True(at > 0, "ConfirmPrompt is not declared the way this test looks for it");

        var end = source.IndexOf("\n    }\n", at, StringComparison.Ordinal);
        var body = source[at..(end < 0 ? source.Length : end)];

        var decided = body.IndexOf("RenamePrompt.Decide(", StringComparison.Ordinal);
        var closed = body.IndexOf("ClosePrompt();", StringComparison.Ordinal);

        Assert.True(decided > 0, "the rename is not checked in ConfirmPrompt at all");
        Assert.True(decided < closed,
            "the bar is torn down before the name is checked, so a refusal has "
            + "nowhere to be shown and the typed name is gone");
    }
}
