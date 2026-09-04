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
                                     "Confirmations.EmptyBin(" })
            Assert.Contains(call, source);
    }
}
