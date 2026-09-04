using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// One casing rule for every label in the application.
///
/// **The window disagreed with itself about how a word is spelled.** The list
/// headings read "name", "type", "size" and "modified"; the column chooser —
/// the context menu you open ON those headings — read "Name", "Type", "Size"
/// and "Modified", the same four words twice, in the one control where
/// somebody compares them. The transfer bar said "cancel" and "dismiss" while
/// BatchRename, Conflict, Settings and Share all said "Cancel", and the close
/// confirmation built in the code-behind said "cancel" and "close anyway".
/// Tooltips and placeholders were lower
/// case throughout, which was a deliberate chrome voice and is what made the
/// disagreement systematic rather than accidental: two houses, no rule, and
/// nothing anywhere that said which one a new label should join.
///
/// The lower-case chrome look is abandoned. The rule is sentence case: the
/// first word is capitalised and no later word is, unless it is a proper noun
/// or a key name — "Open in new tab", never "Open In New Tab".
///
/// WHAT THIS TEST CHECKS, and what it deliberately does not:
///
/// It checks the half a machine can check — that a label's first letter is a
/// capital. The other half, "no later word is capitalised unless it is a proper
/// noun", cannot be checked without a dictionary of every proper noun the
/// application may ever name; the repository already spells Vaktari, Proton
/// Drive, Papirus and Ctrl+0 inside labels, and a rule that flagged those would
/// be turned off within a week. That half lives in review.
///
/// Three kinds of string are outside the rule, and each is skipped for a stated
/// reason rather than by being listed:
///
///   * A label whose first word is not a word — "/home/…", "%ProgramFiles%",
///     "file2 before file10", "2026-07-26 14:30 for everything". These begin
///     with a value, and capitalising a filename or a path changes what it
///     means.
///   * A Run that begins with a space. Those continue a sentence started by the
///     Run before them (" over network"), so their first letter is not the
///     start of anything.
///   * Prose: the confirm sentence, the hint lines under the prompt bar, and
///     the refusal reasons the filesystem layer produces. Those are sentences
///     rather than labels, they are frequently interpolated from a name or a
///     reason decided elsewhere, and none of them is an attribute in a markup
///     file — so nothing here reaches them, by design.
///
/// The sidebar's PLACES / NETWORK / REMOTE / SHARING / RECENT headings are
/// compliant and stay as they are. Upper case there is a typographic device —
/// tiny, medium weight, 1.1 of letter spacing — in the same family as small
/// caps, not a spelling of the word; WindowFloorTests counts the NETWORK
/// literal, so it is pinned from the other side too.
/// </summary>
public sealed class LabelCasingTests
{
    /// <summary>
    /// The attributes that put words in front of a person. Command parameters,
    /// resource keys, geometry and x:Name are not labels and are not here —
    /// the sort buttons take CommandParameter="name" and must go on doing so.
    /// </summary>
    private static readonly HashSet<string> LabelAttributes =
    [
        "Content", "Text", "Header", "ToolTip.Tip", "PlaceholderText", "Watermark",
        "Title", "AutomationProperties.Name", "AutomationProperties.HelpText",
    ];

    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// The words an attribute actually shows, or null when it shows none of its
    /// own. A binding shows whatever the view model hands over — except for the
    /// FallbackValue inside it, which is a literal this file is responsible for
    /// and is exactly where the properties window's lower-case "properties" was
    /// hiding.
    /// </summary>
    private static string? Shown(string value)
    {
        if (!value.StartsWith('{')) return value;

        var fallback = Regex.Match(value, @"FallbackValue=([^,}]*)");

        return fallback.Success ? fallback.Groups[1].Value : null;
    }

    private static bool IsChecked(string shown)
    {
        // A continuation Run. Its sentence began in the Run before it.
        if (shown.Length == 0 || char.IsWhiteSpace(shown[0])) return false;

        // Starts with a glyph, a digit or punctuation: "≡", "+", "·", "/home/…",
        // "%ProgramFiles%", "2026-07-26 14:30 …".
        if (!char.IsLetter(shown[0])) return false;

        // Starts with a word that is not one: "file2 before file10" names a
        // file, and "File2" would be a different file.
        return !shown.Split(' ')[0].Any(char.IsDigit);
    }

    private static IEnumerable<(string File, string Attribute, string Shown)> Labels()
    {
        foreach (var file in RepoSource.UiMarkup())
        {
            // XDocument, not a line scan: an attribute value in the file is
            // written with entities — "&#10;" inside the address bar's tooltip
            // — and the words a person reads are the decoded ones.
            var markup = XDocument.Parse(RepoSource.Ui(file));

            // Root INCLUDED: a Window's own Title is a label, and the
            // properties window's lower-case "properties" was on the root
            // element where a Descendants() walk never looked.
            foreach (var element in markup.Root!.DescendantsAndSelf())
            {
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration) continue;
                    if (attribute.Name.Namespace == Xaml) continue;
                    if (!LabelAttributes.Contains(attribute.Name.LocalName)) continue;

                    if (Shown(attribute.Value) is not { } shown) continue;
                    if (!IsChecked(shown)) continue;

                    yield return (file, attribute.Name.LocalName, shown);
                }
            }
        }
    }

    /// <summary>
    /// The rule itself, over every markup file rather than over the one that
    /// drifted: a rule that covered MainWindow alone would have let the next
    /// lower-case button in through Properties or Share.
    /// </summary>
    [Fact]
    public void Every_label_in_the_markup_is_sentence_case()
    {
        var offenders = Labels()
            .Where(label => !char.IsUpper(label.Shown[0]))
            .Select(label => $"{label.File}: {label.Attribute}=\"{label.Shown}\"")
            .ToList();

        Assert.True(offenders.Count == 0,
            "these labels are not sentence case:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// **The test above passes on an empty file.** It reports offenders, so a
    /// selector that quietly stopped matching — a renamed attribute, a markup
    /// file moved into a folder RepoSource.UiMarkup does not walk — would leave
    /// it green while checking nothing at all. This is the floor under it.
    /// </summary>
    [Fact]
    public void The_scan_reaches_every_markup_file_and_a_useful_number_of_labels()
    {
        var labels = Labels().ToList();

        // At least nine: a markup file moved into a folder the walk does
        // not reach must fail here, and a tenth window must not.
        Assert.True(RepoSource.UiMarkup().Count() >= 9,
                    "a markup file has dropped out of the walk");
        Assert.True(labels.Count > 300, $"only {labels.Count} labels were found");

        // Every window contributes some, so a file dropping out of the walk is
        // a failure rather than a smaller number. App.axaml is styles only.
        foreach (var file in RepoSource.UiMarkup().Where(f => f != "App.axaml"))
            Assert.Contains(labels, label => label.File == file);
    }

    /// <summary>
    /// The four headings by value, not just by casing. The rule above is
    /// satisfied by any capitalised word, and these four are the ones the
    /// chooser — the context menu that opens on these headings — names:
    /// "Name", "Type", "Size", "Modified". The two lists have to keep
    /// saying the same thing.
    /// </summary>
    [Fact]
    public void The_column_headings_match_the_chooser_word_for_word()
    {
        var markup = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        foreach (var (parameter, word) in new[]
                 {
                     ("name", "Name"), ("kind", "Type"),
                     ("size", "Size"), ("modified", "Modified"),
                 })
        {
            var heading = markup.Descendants(Avalonia + "Button")
                .Single(b => (string?)b.Attribute("CommandParameter") == parameter);

            // The first Run holds the word; the second is the sort glyph.
            var run = heading.Descendants(Avalonia + "Run").First();

            Assert.Equal(word, (string?)run.Attribute("Text"));

            // And the chooser offers the same word, so neither can be renamed
            // without the other. The chooser itself, not any MenuItem in the
            // file: Group by and Sort by each name all four of these words too,
            // so a search over every MenuItem is satisfied by them and pins
            // nothing. The header Border's context menu is the only one in the
            // file.
            var chooser = markup.Descendants(Avalonia + "Border.ContextMenu").Single();

            Assert.Contains(chooser.Descendants(Avalonia + "MenuItem"),
                            item => (string?)item.Attribute("Header") == word);
        }
    }

    /// <summary>
    /// The two buttons the finding named. Casing alone would be satisfied by
    /// "Abandon" or "Close", and these are the words four other windows use.
    /// </summary>
    [Fact]
    public void The_transfer_bar_says_Cancel_and_Dismiss()
    {
        var markup = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        string Content(string command)
            => (string?)markup.Descendants(Avalonia + "Button")
                .Single(b => (string?)b.Attribute("Command") == command)
                .Attribute("Content") ?? "";

        Assert.Equal("Dismiss", Content("{Binding DismissOperationStatusCommand}"));
        Assert.Equal("Cancel", Content("{Binding CancelOperationCommand}"));
    }

    /// <summary>
    /// The prompt bar builds its own labels in the code-behind, where no markup
    /// scan reaches them — and it is the bar that carried "cancel" and "rename"
    /// and "delete permanently".
    ///
    /// The sentence beside the buttons is NOT read here: Confirmations writes
    /// it, it opens with a count or a filename, and it is prose.
    /// </summary>
    [Fact]
    public void The_prompt_bar_builds_its_labels_in_sentence_case()
    {
        var source = RepoSource.Ui("MainWindow.axaml.cs");

        var written = Regex.Matches(source, @"Prompt(?:Label\.Text|Confirm\.Content) = \$?""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        // Three captions and six confirm buttons; a regex that stopped matching
        // would otherwise report nothing and pass.
        Assert.Equal(9, written.Count);

        foreach (var label in written)
            Assert.True(char.IsUpper(label[0]), $"the prompt bar writes \"{label}\"");
    }

    /// <summary>
    /// The close confirmation is a real Window built in the code-behind, so
    /// neither the markup walk above nor the prompt-bar scan below reaches its
    /// two buttons. **They read "close anyway" and "cancel" while the four
    /// windows that have a Cancel button all said "Cancel".**
    /// </summary>
    [Fact]
    public void The_close_confirmation_builds_its_buttons_in_sentence_case()
    {
        var built = Regex.Matches(RepoSource.Ui("MainWindow.axaml.cs"),
                                  @"new Button \{ Content = ""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        // Two buttons; a regex that stopped matching would report nothing and
        // pass.
        Assert.Equal(2, built.Count);

        foreach (var label in built)
            Assert.True(char.IsUpper(label[0]), $"the close dialog builds \"{label}\"");
    }
}
