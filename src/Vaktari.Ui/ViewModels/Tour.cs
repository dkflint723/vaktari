namespace Vaktari.Ui.ViewModels;

/// <summary>One line of a tour card: a key or a gesture, and what it does.</summary>
public sealed record TourLine(string Keys, string Does);

/// <summary>One card of the tour: a heading, a sentence, and the keys under it.</summary>
public sealed record TourCard(string Title, string Lead, IReadOnlyList<TourLine> Lines);

/// <summary>
/// The window in three cards, for somebody who has just installed it.
///
/// **A first run wrote a settings file and said nothing.** The sidebar, the
/// split, the search box, the filter and the sheet of keys were all there to
/// be found, and nothing pointed at any of them — in an application whose
/// pitch is that the keyboard reaches all of it. Three cards, not a manual:
/// the window, finding things, and making it yours, each a handful of keys.
///
/// **Every key here is a key on the F1 sheet, and a test holds the two
/// together**: a tour that taught a key the sheet does not list would be
/// teaching a key that has been renamed or removed, which is the failure the
/// sheet's own cross-check exists to prevent. Pointer gestures — a click, a
/// drag — are the one kind of line the sheet's cross-check cannot reach and
/// the tour's does not try to.
///
/// Written out rather than drawn from the sheet, because the sheet says what
/// a key does and a card says why somebody would press it.
/// </summary>
public static class Tour
{
    public static IReadOnlyList<TourCard> Cards { get; } =
    [
        new("The window",
            "Tabs, a second pane when you want one, a sidebar of places and a "
            + "details panel — and the keyboard reaches all of them.",
        [
            new("Ctrl+T", "A new tab"),
            new("F3", "A second pane beside this one, for moving things between two folders"),
            new("Ctrl+B", "Show or hide the sidebar"),
            new("F11", "The details panel, for whatever is selected"),
            new("F6", "Move the keyboard between the listing, the address bar and the sidebar"),
        ]),

        new("Finding things",
            "The address bar takes a path, the search box looks under the folder "
            + "you are in, and the filter narrows what is already listed.",
        [
            new("Ctrl+L", "Type a path"),
            new("Ctrl+F", "Search under this folder — the results are a listing, and every search is kept to come back to"),
            new("Ctrl+I", "Filter this listing as you type"),
            new("Escape", "Clear the filter"),
        ]),

        new("Making it yours",
            "Each pane keeps its own layout and size, the list's columns are "
            + "yours to choose and drag, and F1 lists every key.",
        [
            new("F8", "Switch between the list, the small grid and the large grid"),
            new("Ctrl + scroll", "Resize the pane under the pointer"),
            new("Right-click a heading", "Choose the columns; drag a heading's edge to make it wider"),
            new("Ctrl+Shift+,", "Settings"),
            new("F1", "Every key, on one sheet"),
        ]),
    ];
}
