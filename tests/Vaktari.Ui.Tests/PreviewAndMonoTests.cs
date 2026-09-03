using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Two that live in the markup and the key handler.
///
/// **Escape did not close the preview**, which is the one thing drawn OVER the
/// listing rather than beside it — and Escape is the key everybody tries on a
/// thing that covers something else. Space reopens it, but Space is also how
/// you got there, and a key that only toggles is no help to someone who does
/// not know that.
///
/// **And there were two monospace fonts.** Twenty-four places asked for the
/// generic family "monospace" while two asked for the application's own
/// AppMonoFamily, so the font chosen in Settings reached two labels in the
/// details pane and nothing else — not the batch-rename pattern box, not the
/// conflict window's paths, not the nine monospace fields in Properties.
/// </summary>
public sealed class PreviewAndMonoTests
{
    private static string Window(string declaration)
        => RepoSource.Body(RepoSource.Ui("MainWindow.axaml.cs"), declaration);

    /// <summary>
    /// The topmost dismissible thing goes first, and it takes the key with it:
    /// Escape pressed to put a preview away must not also throw away a filter
    /// the person is still using.
    /// </summary>
    [Fact]
    public void Escape_closes_the_preview_before_it_clears_anything()
    {
        var body = Window("private void OnWindowKeyDown(object? sender, KeyEventArgs e)");

        var preview = body.IndexOf("IsPreviewVisible: true", StringComparison.Ordinal);
        var clears = body.IndexOf("DismissInListing()", StringComparison.Ordinal);

        Assert.True(preview > 0, "Escape never looks at the preview");
        Assert.True(clears > 0, "the filter and cut clear has moved or gone");
        Assert.True(preview < clears,
                    "the preview is closed after the filter is already cleared");

        // And the key stops there, or the clear below runs anyway and the
        // ordering above buys nothing.
        var handled = body.IndexOf("e.Handled = true;", preview, StringComparison.Ordinal);

        Assert.True(handled > preview && handled < clears,
                    "closing the preview does not mark the key handled");
    }

    /// <summary>
    /// One monospace source. The generic family is not wrong on its own — it is
    /// wrong beside a resource that the font setting and the theme both write
    /// to, because then half the monospace text follows the setting and half
    /// does not.
    /// </summary>
    [Fact]
    public void No_markup_asks_for_the_generic_monospace_family()
    {
        var offenders = RepoSource.UiMarkup()
            .Where(name => RepoSource.Ui(name).Contains(
                "FontFamily=\"monospace\"", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The other half. Deleting the attribute satisfies the rule above just as
    /// well as converting it, and loses the monospace entirely -- so the count
    /// is a floor rather than a presence check: dropping even one of the
    /// twenty-six fails here. The number is expected to move when monospace
    /// text is genuinely added or removed; it is here to catch the sites going
    /// away quietly, not to freeze the markup.
    /// </summary>
    [Fact]
    public void And_asks_for_the_application_s_own_everywhere_it_used_to()
    {
        var uses = RepoSource.UiMarkup().Sum(
            name => Count(RepoSource.Ui(name), "FontFamily=\"{DynamicResource AppMonoFamily}\""));

        Assert.True(uses >= 26, $"only {uses} of the 26 monospace fields ask for the app font");
    }

    /// <summary>And in every window that had the generic one, not just the two
    /// that were already right.</summary>
    [Theory]
    [InlineData("MainWindow.axaml")]
    [InlineData("BatchRenameWindow.axaml")]
    [InlineData("ConflictWindow.axaml")]
    [InlineData("ConnectionWindow.axaml")]
    [InlineData("PropertiesWindow.axaml")]
    [InlineData("ShareWindow.axaml")]
    [InlineData("ShortcutsWindow.axaml")]
    public void And_asks_for_it_in_each_window(string markup)
        => Assert.Contains(
            "FontFamily=\"{DynamicResource AppMonoFamily}\"",
            RepoSource.Ui(markup));

    private static int Count(string source, string needle)
    {
        var found = 0;

        for (var at = source.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            found++;

        return found;
    }

    /// <summary>
    /// Nothing defines that resource in App.axaml — every one of these is
    /// created at runtime by ThemeApplier — so the key has to be written there
    /// or all twenty-six sites silently fall back to the inherited font.
    /// </summary>
    [Fact]
    public void And_something_actually_defines_it()
        => Assert.Contains("target[\"AppMonoFamily\"]", RepoSource.Ui("ThemeApplier.cs"));
}
