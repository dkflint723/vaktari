using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What a launch does with the paths on its command line before anything else
/// sees them.
///
/// **Nothing made a relative path absolute.** "vaktari ." handed "." to the
/// copy already running, which read it against its own working directory and
/// opened the wrong folder, and "vaktari sub" was dropped there without a word.
/// Only the launch knows where it was started.
/// </summary>
public sealed class CommandLinePathTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "vaktari-started-here");

    [Fact]
    public void A_relative_path_is_made_absolute_against_where_the_launch_started()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "elsewhere");

        Assert.Equal(
            [Base, Path.Combine(Base, "sub"), Path.Combine(Base, "sub", "notes.txt"), elsewhere],
            Program.ResolveArguments(
                [".", "sub", Path.Combine("sub", "notes.txt"), elsewhere], () => Base));
    }

    /// <summary>
    /// A URI is not a relative path, whatever its local form looks like, and one
    /// this process cannot open must still reach OpenPaths as it came — that is
    /// where it is refused out loud rather than turned into a folder under the
    /// working directory.
    /// </summary>
    [Fact]
    public void A_uri_is_left_as_it_came()
    {
        string[] uris = ["file:///x", "trash:///y", "sftp://box/home/me"];

        Assert.Equal(uris, Program.ResolveArguments(uris, () => Base));
    }

    /// <summary>
    /// What .NET throws for the working directory of a terminal whose folder
    /// was deleted from under it: getcwd fails with ENOENT.
    /// </summary>
    private static string Deleted() => throw new FileNotFoundException("No such file or directory");

    /// <summary>
    /// **A launch from a deleted folder died with every argument absolute.**
    /// The working directory was read up front, on every launch, and on Linux
    /// it cannot be read once the folder has gone — the start threw before the
    /// lock or the handover, although nothing on its command line needed it.
    /// </summary>
    [Fact]
    public void A_launch_with_nothing_relative_never_asks_where_it_was_started()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "somewhere");
        string[] arguments = [absolute, "file:///x", "trash:///y"];
        var asked = 0;

        Assert.Equal(arguments, Program.ResolveArguments(arguments, () => { asked++; return Deleted(); }));
        Assert.Equal(0, asked);
    }

    /// <summary>
    /// And a relative argument whose base cannot be read goes on as it came —
    /// what every launch did before any argument was resolved — rather than
    /// taking the launch down with it.
    /// </summary>
    [Fact]
    public void A_relative_path_with_nowhere_to_resolve_against_goes_on_as_it_came()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "somewhere");

        Assert.Equal(["sub", absolute], Program.ResolveArguments(["sub", absolute], Deleted));
    }

    /// <summary>
    /// **Too many paths opened a second window.** A handover over the cap came
    /// back as "nobody answered", and with folders to show that opens a window
    /// of its own on the shared session file. It is its own answer now, and
    /// the launch stops there.
    /// </summary>
    [Fact]
    public void A_launch_with_too_much_to_hand_over_stops_instead_of_opening_a_window()
    {
        Assert.NotNull(Program.AfterHandover(SingleInstance.Handover.TooLarge, 20_000));

        // Nobody answering with folders to show is still the one case that does.
        Assert.Null(Program.AfterHandover(SingleInstance.Handover.NoAnswer, 1));
    }

    /// <summary>
    /// **Granted before the handover, or the window it raises only flashes.**
    /// Windows refuses the foreground to a background process, which the
    /// running copy is by then; only the launch holds the right to pass on.
    /// Read from the source because the grant is the system's to honour and
    /// nothing a test can observe — what can be held is that it is made, and
    /// made first.
    /// </summary>
    [Fact]
    public void The_launch_lets_the_running_window_come_forward_before_handing_over()
    {
        var source = RepoSource.Ui("Program.cs");

        var grant = source.IndexOf("Vaktari.Windows.ForegroundHandover.Allow();", StringComparison.Ordinal);
        var handover = source.IndexOf("SingleInstance.TryForward(paths)", StringComparison.Ordinal);

        Assert.True(grant >= 0, "the launch no longer grants the foreground");
        Assert.True(handover > grant, "the foreground is granted after the handover");
    }
}
