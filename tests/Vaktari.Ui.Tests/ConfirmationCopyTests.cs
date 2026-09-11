using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The sentence you are asked to agree to before something is destroyed.
///
/// **A count is not an identification.** "permanently delete 1 item(s)?" asks
/// for approval of something irreversible without saying what it is — and one
/// item is exactly the case where naming it costs nothing. Select the wrong
/// row, press Shift+Delete, read a sentence that would be identical for any
/// file on the machine, and the confirmation has done nothing except add a
/// keystroke.
///
/// The parenthesised plural went with it. A sentence that hedges its own
/// grammar reads as machine output, and the moment you are being asked to
/// destroy something is the wrong moment to sound like a dialog box from 1996.
/// </summary>
public sealed class ConfirmationCopyTests
{
    private static FileEntry Entry(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1,
               DateTimeOffset.UnixEpoch, EntryFlags.None);

    private static TrashedItem Binned(string path, bool isDirectory = false)
        => new("t1", path, "p", DateTimeOffset.UnixEpoch, 4, isDirectory);

    private static CopyAcrossPlan Across(string[] missing, string[] replacing, string to = "/home/me/backup")
        => new(to, missing, replacing);

    [Fact]
    public void Copying_one_file_across_names_it_and_where_it_goes()
        => Assert.Equal("copy notes.txt to /home/me/backup?",
                        Confirmations.CopyAcross(Across(["/home/me/notes.txt"], [])));

    /// <summary>**The replacing is said out loud**, because it is the one
    /// part an undo cannot take back.</summary>
    [Fact]
    public void Copying_across_says_what_it_replaces_for_good()
    {
        Assert.Equal("copy notes.txt to /home/me/backup? it replaces the older one there for good",
                     Confirmations.CopyAcross(Across([], ["/home/me/notes.txt"])));
        Assert.Equal("copy 3 items to /home/me/backup? 1 of them replaces an older file there for good",
                     Confirmations.CopyAcross(Across(["/home/me/a", "/home/me/b"], ["/home/me/c"])));
        Assert.Equal("copy 3 items to /home/me/backup? 2 of them replace older files there for good",
                     Confirmations.CopyAcross(Across(["/home/me/a"], ["/home/me/b", "/home/me/c"])));
    }

    /// <summary>
    /// **By the destination's path, not its name.** A folder and its backup
    /// are called the same, and "copy 3 items to Photos?" read the same in
    /// either direction.
    /// </summary>
    [Fact]
    public void Two_folders_of_one_name_are_told_apart()
    {
        Assert.Equal("copy a.jpg to /media/stick/Photos?",
                     Confirmations.CopyAcross(Across(["/home/me/Photos/a.jpg"], [], to: "/media/stick/Photos")));
        Assert.Equal("copy a.jpg to /home/me/Photos?",
                     Confirmations.CopyAcross(Across(["/media/stick/Photos/a.jpg"], [], to: "/home/me/Photos")));
    }

    /// <summary>A long destination is cut in the middle, so its start and its
    /// own name both survive.</summary>
    [Fact]
    public void A_long_destination_keeps_its_start_and_its_own_name()
    {
        var deep = "/home/me/" + string.Join("/", Enumerable.Repeat("folder", 10)) + "/Photos";
        var said = Confirmations.CopyAcross(Across(["/x/a.jpg"], [], to: deep));

        Assert.StartsWith("copy a.jpg to /home/me/", said);
        Assert.EndsWith("/Photos?", said);
        Assert.Contains("…", said);
    }

    [Fact]
    public void What_copying_across_left_alone_is_named_or_counted()
    {
        var plan = Across(["/home/me/a.txt", "/home/me/b.txt"], []);

        Assert.Null(Confirmations.LeftAlone(plan));

        plan.Decide(new FileConflict("/home/me/a.txt", "/home/me/backup/a.txt"));

        Assert.Equal("left a.txt alone: it changed after the prompt", Confirmations.LeftAlone(plan));

        plan.Decide(new FileConflict("/home/me/b.txt", "/home/me/backup/b.txt"));

        Assert.Equal("left 2 items alone: they changed after the prompt", Confirmations.LeftAlone(plan));
    }

    [Fact]
    public void What_copying_across_leaves_out_is_said_with_why()
    {
        Assert.Null(Confirmations.LeftOut(Across(["/home/me/a"], [])));

        Assert.Equal("left out of the copy: inner, which holds the other side",
            Confirmations.LeftOut(new CopyAcrossPlan("/home/me/inner/deeper", [], [],
                [new Withheld("/home/me/inner", WithheldBecause.HoldsTheOtherSide)])));

        Assert.Equal("left out of the copy: \"report \", whose name Windows cannot open",
            Confirmations.LeftOut(new CopyAcrossPlan("/x", [], [],
                [new Withheld("/home/me/report ", WithheldBecause.NameWindowsCannotOpen)])));

        Assert.Equal("left out of the copy: 2 names Windows cannot open",
            Confirmations.LeftOut(new CopyAcrossPlan("/x", [], [],
                [new Withheld("/home/me/a ", WithheldBecause.NameWindowsCannotOpen),
                 new Withheld("/home/me/b.", WithheldBecause.NameWindowsCannotOpen)])));
    }

    [Fact]
    public void One_file_is_named_rather_than_counted()
    {
        var said = Confirmations.Delete([Entry("report.docx")]);

        Assert.Contains("report.docx", said);

        // The load-bearing half: "1 item" and "1 item(s)" both contain "item",
        // so this fails on the old sentence and on any regression to a count.
        Assert.DoesNotContain("item", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_are_counted_without_the_parenthesised_plural()
    {
        var said = Confirmations.Delete([Entry("a.txt"), Entry("b.txt"), Entry("c.txt")]);

        Assert.Contains("3 items", said);
        Assert.DoesNotContain("(s)", said);
    }

    [Fact]
    public void Moving_one_to_the_bin_names_it_too()
    {
        var said = Confirmations.MoveToBin([Entry("holiday.jpg")]);

        Assert.Contains("holiday.jpg", said);
        Assert.DoesNotContain("item", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Emptying_the_bin_names_its_one_item()
    {
        var said = Confirmations.EmptyBin(
            [Binned(Path.Combine(Path.GetTempPath(), "taxes.pdf"))]);

        Assert.Contains("taxes.pdf", said);
        Assert.DoesNotContain("item", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// A binned folder's remembered path can carry a trailing separator, and
    /// Path.GetFileName answers "" for that — which would leave a gap where the
    /// name goes and read as a sentence with a word missing.
    /// </summary>
    [Fact]
    public void A_folder_keeps_its_name_past_a_trailing_separator()
    {
        var said = Confirmations.EmptyBin(
            [Binned(Path.Combine(Path.GetTempPath(), "holiday") + Path.DirectorySeparatorChar,
                    isDirectory: true)]);

        Assert.Contains("holiday", said);
    }

    /// <summary>
    /// The prompt bar is one horizontal row with the confirm button to the
    /// right of this text, so a very long name would push "Delete permanently"
    /// off the window — leaving a question with no way to answer it but the
    /// keyboard.
    /// </summary>
    [Fact]
    public void A_very_long_name_is_shortened_so_the_button_stays_on_screen()
    {
        var said = Confirmations.Delete([Entry(new string('x', 400) + ".pdf")]);

        Assert.True(said.Length < 140, $"the sentence is {said.Length} characters");

        // Elided in the middle, so the extension survives: ".pdf" against
        // ".exe" is the part that changes what deleting it means.
        Assert.EndsWith(".pdf?" + " this cannot be undone", said);
    }

    /// <summary>Nothing selected still reads as a sentence rather than as a
    /// gap, even though no prompt should open in that case.</summary>
    [Fact]
    public void A_nameless_subject_falls_back_to_counting()
    {
        Assert.Equal("1 item", Confirmations.Subject(1, null));
        Assert.Equal("1 item", Confirmations.Subject(1, "   "));
        Assert.Equal("0 items", Confirmations.Subject(0, null));
    }

    /// <summary>
    /// And the window asks it, rather than assembling its own sentence. Three
    /// call sites built that string by hand, which is how they came to disagree
    /// with each other in the first place.
    /// </summary>
    [AvaloniaFact]
    public void The_prompt_bar_asks_for_the_sentence()
    {
        var source = RepoSource.Ui("MainWindow.axaml.cs");

        Assert.DoesNotContain("item(s)? this cannot be undone", source);
        Assert.DoesNotContain("item(s) to {Naming.TheBin}", source);

        foreach (var call in new[] { "Confirmations.Delete(", "Confirmations.MoveToBin(",
                                     "Confirmations.EmptyBin(", "Confirmations.CopyAcross(" })
            Assert.Contains(call, source);
    }
}
