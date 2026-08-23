using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Tab-completion in the path box.
///
/// **Written after it turned out not to work on Windows at all.** The splitter
/// looked for `/` and nothing else, so a path typed the way Windows spells one
/// — `C:\Users\...` — had no separator it could see, the directory came back
/// empty, and Tab silently offered nothing. Worse, the completer carried its
/// own expander that understood only `~`, so `%LOCALAPPDATA%\GOG.com` completed
/// nothing while typing while Enter navigated there correctly: the box
/// appearing not to understand a path it understood perfectly well.
/// </summary>
public sealed class PathCompleterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-complete-" + Guid.NewGuid().ToString("N"));

    public PathCompleterTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Galaxy"));
        Directory.CreateDirectory(Path.Combine(_root, "Games"));
        Directory.CreateDirectory(Path.Combine(_root, "Music"));
        Directory.CreateDirectory(Path.Combine(_root, ".hidden"));
    }

    public void Dispose()
    {
        // Only what this test built, under its own root.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// The one that would have caught it: a path spelled the way Windows spells
    /// one, which on Windows is what every path looks like.
    /// </summary>
    [Fact]
    public void A_path_written_with_backslashes_completes()
    {
        var completed = new PathCompleter().Complete(_root + @"\Mus");

        Assert.NotNull(completed);
        Assert.Contains("Music", completed);
    }

    /// <summary>And the other spelling, which Windows also accepts and people
    /// paste from scripts.</summary>
    [Fact]
    public void A_path_written_with_forward_slashes_completes()
    {
        var completed = new PathCompleter().Complete(_root.Replace('\\', '/') + "/Mus");

        Assert.NotNull(completed);
        Assert.Contains("Music", completed);
    }

    /// <summary>
    /// **The spelling that came in is the spelling that goes out.** Handing
    /// back `C:\Users\me/Music/` for something typed with backslashes looks
    /// like a bug even though the path is valid.
    /// </summary>
    [Fact]
    public void The_spelling_is_kept()
    {
        var backslashes = new PathCompleter().Complete(_root + @"\Mus");

        Assert.NotNull(backslashes);
        Assert.EndsWith(@"Music\", backslashes);
        Assert.DoesNotContain('/', backslashes);
    }

    /// <summary>
    /// A variable resolves while completing, exactly as it does when
    /// navigating. This is the case that was reported: the path bar understood
    /// it on Enter and appeared not to on Tab.
    /// </summary>
    [Fact]
    public void A_variable_is_expanded_before_completing()
    {
        // TEMP is the one variable certain to exist on both platforms in CI,
        // and the fixture lives under it.
        var typed = Path.Combine("%TEMP%", Path.GetFileName(_root), "Gal");

        var completed = new PathCompleter().Complete(typed);

        Assert.NotNull(completed);
        Assert.Contains("Galaxy", completed);
    }

    /// <summary>
    /// Several candidates extend only as far as they agree, which is the shell
    /// convention this exists to follow — "Gal" must not jump to Galaxy while
    /// Galactic is equally likely.
    /// </summary>
    [Fact]
    public void Ambiguous_input_extends_only_to_what_every_candidate_agrees_on()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Galactic"));

        var completed = new PathCompleter().Complete(_root + @"\Gal");

        Assert.NotNull(completed);
        Assert.EndsWith(@"Gala\", completed);
    }

    /// <summary>
    /// With nothing left to extend — what was typed already IS everything the
    /// candidates agree on — further presses cycle instead of standing still.
    /// </summary>
    [Fact]
    public void When_there_is_nothing_left_to_extend_it_cycles()
    {
        var completer = new PathCompleter();

        var first = completer.Complete(_root + @"\Ga");
        var second = completer.Complete(first!);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);

        foreach (var offered in new[] { first!, second! })
            Assert.True(
                offered.Contains("Galaxy") || offered.Contains("Games"),
                $"expected one of the two candidates, got {offered}");
    }

    /// <summary>A trailing separator means "everything in here".</summary>
    [Fact]
    public void A_trailing_separator_offers_the_contents()
    {
        var completed = new PathCompleter().Complete(_root + @"\");

        Assert.NotNull(completed);
        Assert.True(
            completed.Contains("Galaxy") || completed.Contains("Games") || completed.Contains("Music"),
            $"expected one of the folders, got {completed}");
    }

    /// <summary>Nothing matching is null rather than a guess.</summary>
    [Fact]
    public void Nothing_matching_offers_nothing()
        => Assert.Null(new PathCompleter().Complete(_root + @"\Zzz"));

    /// <summary>
    /// A bare drive letter is not a folder to complete inside — it means
    /// wherever the process happens to be on that drive, which is nobody's
    /// intent. Only meaningful on Windows; elsewhere there is no such spelling.
    /// </summary>
    [Fact]
    public void A_bare_drive_letter_does_not_complete_from_the_working_directory()
    {
        if (!OperatingSystem.IsWindows()) return;

        var completed = new PathCompleter().Complete(@"C:\Wind");

        // Whatever it offers must be under the root of C:, not under wherever
        // this test process happens to be.
        if (completed is not null)
            Assert.StartsWith(@"C:\", completed, StringComparison.OrdinalIgnoreCase);
    }
}
