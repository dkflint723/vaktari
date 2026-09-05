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
/// nothing while typing while Enter navigated there correctly.
///
/// **Written the first time with backslashes throughout, which failed the whole
/// Linux build.** A backslash is an ordinary character in a Linux filename, so
/// those tests were not testing the other spelling there — they were asking for
/// a file that genuinely did not exist. Anything spelling-specific is now
/// guarded by platform, and the variable case sets its own variable rather than
/// borrowing %TEMP%, which Linux does not define.
/// </summary>
public sealed class PathCompleterTests : IDisposable
{
    private const string RootVariable = "VAKTARI_TEST_COMPLETE_ROOT";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-complete-" + Guid.NewGuid().ToString("N"));

    private static char Slash => Path.DirectorySeparatorChar;

    public PathCompleterTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Galaxy"));
        Directory.CreateDirectory(Path.Combine(_root, "Games"));
        Directory.CreateDirectory(Path.Combine(_root, "Music"));
        Directory.CreateDirectory(Path.Combine(_root, ".hidden"));

        Environment.SetEnvironmentVariable(RootVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RootVariable, null);

        // Only what this test built, under its own root.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// The one that would have caught the original: a path spelled the way this
    /// platform spells one.
    /// </summary>
    [Fact]
    public void A_path_in_the_platforms_own_spelling_completes()
    {
        var completed = new PathCompleter().Complete(_root + Slash + "Mus");

        Assert.NotNull(completed);
        Assert.Contains("Music", completed);
    }

    /// <summary>
    /// Forward slashes complete on both platforms — they are the only spelling
    /// on Linux, and Windows accepts them too, which is how a path pasted from
    /// a script arrives.
    /// </summary>
    [Fact]
    public void A_path_written_with_forward_slashes_completes()
    {
        var completed = new PathCompleter().Complete(_root.Replace('\\', '/') + "/Mus");

        Assert.NotNull(completed);
        Assert.Contains("Music", completed);
    }

    /// <summary>
    /// **The spelling that came in is the spelling that goes out.** Handing
    /// back a mix of the two looks like a bug even where the path is valid.
    /// Windows only: on Linux a backslash is part of a name, not a separator.
    /// </summary>
    [Fact]
    public void The_spelling_is_kept()
    {
        if (!OperatingSystem.IsWindows()) return;

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
        var completed = new PathCompleter().Complete($"%{RootVariable}%{Slash}Gal");

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

        var completed = new PathCompleter().Complete(_root + Slash + "Gal");

        Assert.NotNull(completed);
        Assert.EndsWith("Gala" + Slash, completed);
    }

    /// <summary>
    /// With nothing left to extend — what was typed already IS everything the
    /// candidates agree on — further presses cycle instead of standing still.
    /// </summary>
    [Fact]
    public void When_there_is_nothing_left_to_extend_it_cycles()
    {
        var completer = new PathCompleter();

        var first = completer.Complete(_root + Slash + "Ga");
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
        var completed = new PathCompleter().Complete(_root + Slash);

        Assert.NotNull(completed);
        Assert.True(
            completed.Contains("Galaxy") || completed.Contains("Games") || completed.Contains("Music"),
            $"expected one of the folders, got {completed}");
    }

    /// <summary>Nothing matching is null rather than a guess.</summary>
    [Fact]
    public void Nothing_matching_offers_nothing()
        => Assert.Null(new PathCompleter().Complete(_root + Slash + "Zzz"));

    /// <summary>
    /// A bare drive letter is not a folder to complete inside — it means
    /// wherever the process happens to be on that drive. Windows only; no such
    /// spelling exists elsewhere.
    /// </summary>
    [Fact]
    public void A_bare_drive_letter_does_not_complete_from_the_working_directory()
    {
        if (!OperatingSystem.IsWindows()) return;

        var completed = new PathCompleter().Complete(@"C:\Wind");

        if (completed is not null)
            Assert.StartsWith(@"C:\", completed, StringComparison.OrdinalIgnoreCase);
    }

    // ---- and the same candidates, all at once -------------------------------

    /// <summary>
    /// **Every candidate was computed and one was handed back.** A dropdown
    /// needs the list Tab already built, in the order Tab offers it — so this
    /// is the same three folders, as full paths, rather than a fresh idea about
    /// what matches.
    /// </summary>
    [Fact]
    public void Everything_tab_would_offer_can_be_asked_for_at_once()
    {
        var offered = PathCompleter.Suggestions(_root + Slash + "Ga", 10);

        Assert.Equal(
            ["Galaxy", "Games"],
            offered.Select(p => Path.GetFileName(p.TrimEnd(Slash, '/'))));

        // Full paths, not names: the caller puts one straight into the box.
        Assert.All(offered, p => Assert.StartsWith(_root, p, StringComparison.Ordinal));
    }

    /// <summary>The hidden-folder rule is the completer's, and asking for the
    /// list must not be a way around it.</summary>
    [Fact]
    public void A_dotted_folder_still_has_to_be_asked_for()
    {
        Assert.DoesNotContain(
            PathCompleter.Suggestions(_root + Slash, 10),
            p => p.Contains(".hidden", StringComparison.Ordinal));

        Assert.Contains(
            PathCompleter.Suggestions(_root + Slash + ".h", 10),
            p => p.Contains(".hidden", StringComparison.Ordinal));
    }

    /// <summary>
    /// Capped, because the caller is a dropdown and a folder can hold thousands
    /// of children. A list that has to be scrolled past the window's edge is
    /// not an offer.
    /// </summary>
    [Fact]
    public void The_offer_stops_at_what_was_asked_for()
        => Assert.Equal(2, PathCompleter.Suggestions(_root + Slash, 2).Count);

    /// <summary>
    /// **The reason this is static rather than a reader on the cycle's own
    /// list.** The box asks on every keystroke, and a Tab cycle is anchored to
    /// the directory and fragment it was built for — so an ask that rebuilt
    /// that list would reset the cycle position, and Tab would re-offer the
    /// first candidate forever instead of advancing.
    ///
    /// **This is a GUARD: it cannot fail while Suggestions is static**, because
    /// a static method has no cycle to reach. It exists to fail on the day
    /// somebody moves it onto the instance to save a directory read — which is
    /// exactly the change that looks like an optimisation and is not.
    /// </summary>
    [Fact]
    public void Asking_what_is_on_offer_does_not_disturb_a_cycle_in_progress()
    {
        var completer = new PathCompleter();

        var first = completer.Complete(_root + Slash + "Ga");

        PathCompleter.Suggestions(_root + Slash + "Ga", 10);
        PathCompleter.Suggestions(first!, 10);

        var second = completer.Complete(first!);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    /// <summary>Nothing to search in is an empty offer rather than a guess at
    /// the working directory — and asking for none of them is none of them,
    /// which is the cap answering at its floor rather than a separate rule
    /// about it.</summary>
    [Fact]
    public void Nothing_to_search_in_offers_nothing()
    {
        Assert.Empty(PathCompleter.Suggestions("", 10));
        Assert.Empty(PathCompleter.Suggestions(_root + Slash + "Zzz", 10));
        Assert.Empty(PathCompleter.Suggestions(_root + Slash, 0));
    }
}
