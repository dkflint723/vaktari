using Vaktari.Ui.Input;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// One line of a tour card: the command it teaches, or a gesture no keymap
/// holds, and what it does.
/// </summary>
public sealed record TourLine(string? Command, string Gesture, string Does)
{
    /// <summary>A command's line, printed with whatever keys it has now.</summary>
    public static TourLine Of(string command, string does) => new(command, "", does);

    /// <summary>A key or gesture no keymap can move — Escape, a scroll, a click.</summary>
    public static TourLine Fixed(string gesture, string does) => new(null, gesture, does);

    /// <summary>
    /// What the line prints on the left. **Read when the card is shown**, from
    /// the keymap in force: a tour that taught the key a command had before
    /// somebody moved it would be teaching a key that does nothing.
    /// </summary>
    public string Keys => Command is { } id
        ? Keymap.Current.Readable(id) is { Length: > 0 } printed ? printed : "no key yet"
        : Gesture;
}

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
/// **Each line names a command, not a key.** The keys are the keymap's, so a
/// tour opened after somebody moved Search to another key teaches that key;
/// the handful of fixed lines — Escape, a scroll, a right-click — are the ones
/// no keymap holds.
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
            TourLine.Of("NewTab", "A new tab"),
            TourLine.Of("ToggleSplit", "A second pane beside this one, for moving things between two folders"),
            TourLine.Of("Sidebar", "Show or hide the sidebar"),
            TourLine.Of("ToggleInfo", "The details panel, for whatever is selected"),
            TourLine.Of("NextRegion", "Move the keyboard between the listing, the address bar and the sidebar"),
        ]),

        new("Finding things",
            "The address bar takes a path, the search box looks under the folder "
            + "you are in, and the filter narrows what is already listed.",
        [
            TourLine.Of("EditPath", "Type a path"),
            TourLine.Of("Search", "Search under this folder — the results are a listing, and every search is kept to come back to"),
            TourLine.Of("ToggleFilter", "Filter this listing as you type"),
            TourLine.Fixed("Escape", "Clear the filter"),
        ]),

        new("Making it yours",
            "Each pane keeps its own layout and size, the list's columns are "
            + "yours to choose and drag, and every key can be changed.",
        [
            TourLine.Of("ToggleView", "Switch between the list, the small grid and the large grid"),
            TourLine.Fixed("Ctrl + scroll", "Resize the pane under the pointer"),
            TourLine.Fixed("Right-click a heading", "Choose the columns; drag a heading's edge to make it wider"),
            TourLine.Of("OpenSettings", "Settings — Keyboard is where a command's keys are changed"),
            TourLine.Of("ShowShortcuts", "Every key, on one sheet"),
        ]),
    ];
}
