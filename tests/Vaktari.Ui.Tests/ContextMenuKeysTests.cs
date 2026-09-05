using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The letter that picks a menu row.
///
/// **Ninety-one menu rows and not one access key.** Every menu in the
/// application — the listing's, the sidebar's, the column chooser's, the tab
/// strip's — was written with a plain Header, so the only way through an open
/// menu was the arrow keys, one row at a time, down a listing menu that is
/// thirty-nine rows long. Avalonia does not offer a type-to-select fallback in
/// menus: with no marker in the header there is no key at all, and "Properties"
/// — the last row in that menu — was reachable only by walking to it. (The two
/// counts are measured: ninety-one was the number of rows with a literal header
/// that <see cref="Menus"/> reached across both markup files, the same at the
/// commit before that change as at it, and thirty-nine is the listing menu's
/// own row count. The first has moved since — ninety-eight rows over fifteen
/// menus today, the column chooser having gained a second copy in Arrange —
/// which is why the floors below are floors rather than counts.)
///
/// Measured rather than assumed, on Avalonia 12.1 in a headless window: a
/// MenuItem whose Header is "_Open" realizes an AccessText reporting AccessKey
/// 'O', and one whose Header is "Open" reports none. That measurement is what
/// <see cref="MenuLabels_reads_a_header_exactly_as_the_theme_draws_it"/>
/// keeps honest — the rules above it are about the markup, and a framework that
/// stopped parsing the marker would leave every one of them green.
///
/// WHAT THE RULES ARE, and why the obvious one is not among them:
///
/// "Every row has a key" cannot be a rule here. The listing menu has 36 rows
/// with a literal header, and between them their words contain 24 of the 26
/// letters — no j and no q. Thirty-six rows cannot have thirty-six distinct
/// keys out of twenty-four letters, and a maximum matching of rows to letters
/// over the whole menu tops out at exactly 24 — computed, not guessed — so
/// twelve rows must go without one or share.
///
/// Sharing is the worse half. **Measured on Avalonia 12.1's own
/// AccessKeyHandler: a key two rows answer to MOVES THE HIGHLIGHT and picks
/// nothing.** Two rows keyed 'o' and one keyed 'y', and the handler's
/// ProcessKey answers MoreMatches for 'o' — focusing the first row, then the
/// second, then the first again, press after press, with no Click raised and
/// the menu still open — and LastMatch for 'y', which raises the Click and
/// closes the menu. <see cref="A_key_two_rows_share_moves_the_highlight_rather_than_picking"/>
/// is that measurement. So a shared key stops being a shortcut and becomes a
/// two-step selection that looks like the menu ignoring you.
///
/// So the rules are: no two rows in a menu share a key, and a row goes without
/// one only when every letter of its FIRST word is already spoken for in that
/// menu. The asymmetry is deliberate. A key may be taken from a later word —
/// "Open _with", "Compress to _ZIP" — because the underline says where it is.
/// But the ABSENCE of a key is only excused by the word the row leads with,
/// which is where the eye goes; a row called "Windows menu" that could have had
/// the "u" of "menu" and took nothing is a row nobody can reach, and the rule
/// has to say so.
///
/// In every menu smaller than the alphabet — which is all of them but one —
/// these two rules together come out as "every row has its own key".
/// </summary>
public sealed class ContextMenuKeysTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>A menu, and the rows a person sees when it opens.</summary>
    private sealed record Menu(string File, string Where, IReadOnlyList<XElement> Rows);

    /// <summary>
    /// What to call a menu in a failure message. The header of the MenuItem
    /// that hosts a submenu, or the x:Name / x:DataType a ContextMenu is
    /// recognisable by — anything that lets somebody find it in the file.
    /// </summary>
    private static string Describe(XElement host)
    {
        var mark = (string?)host.Attribute("Header")
                   ?? (string?)host.Attribute(Xaml + "Name")
                   ?? (string?)host.Attribute(Xaml + "DataType")
                   ?? "";

        return (host.Name.LocalName + " " + MenuLabels.Plain(mark)).Trim();
    }

    /// <summary>
    /// Every menu in every markup file: the four popup hosts, plus a MenuItem
    /// that has MenuItems of its own, because a submenu is its own menu and its
    /// keys live in their own namespace.
    /// </summary>
    private static List<Menu> Menus()
    {
        var menus = new List<Menu>();

        foreach (var file in RepoSource.UiMarkup())
        {
            var markup = XDocument.Parse(RepoSource.Ui(file));

            foreach (var host in markup.Root!.DescendantsAndSelf())
            {
                if (host.Name.LocalName is not
                    ("ContextMenu" or "MenuFlyout" or "Menu" or "MenuItem")) continue;

                var rows = host.Elements(Avalonia + "MenuItem").ToList();

                if (rows.Count == 0) continue;

                menus.Add(new Menu(file, Describe(host), rows));
            }
        }

        return menus;
    }

    /// <summary>
    /// The header a person reads, or null when the row does not carry one in
    /// the markup: a bound header is written by a view model — "Move to Recycle
    /// Bin", "Undo the rename" — and a marker cannot be put into the markup for
    /// a string that is not there. A row built from an ItemTemplate is the same
    /// case.
    /// </summary>
    private static string? Literal(XElement row)
        => (string?)row.Attribute("Header") is { } header && !header.StartsWith('{')
            ? header
            : null;

    /// <summary>
    /// A row that is never pickable. The three of them are placeholders shown
    /// while something downloads — "Installing copyparty…" — and a key on a row
    /// that cannot be chosen is a key that does nothing.
    /// </summary>
    private static bool NeverPickable(XElement row)
        => (string?)row.Attribute("IsEnabled") == "False";

    /// <summary>The letters of the word the row leads with.</summary>
    private static IEnumerable<char> FirstWord(string header)
        => MenuLabels.Plain(header).Split(' ')[0]
            .Where(char.IsLetter)
            .Select(char.ToLowerInvariant);

    // ---- the rules ---------------------------------------------------------

    /// <summary>
    /// **A shared key cycles rather than picks.** Two rows answering to the
    /// same letter turn one press into "move the highlight" and a second press
    /// into "move it again", which reads as the key not working — measured, and
    /// pinned by
    /// <see cref="A_key_two_rows_share_moves_the_highlight_rather_than_picking"/>.
    /// </summary>
    [Fact]
    public void No_two_rows_in_one_menu_answer_to_the_same_key()
    {
        var clashes = new List<string>();

        foreach (var menu in Menus())
        {
            var taken = new Dictionary<char, string>();

            foreach (var row in menu.Rows)
            {
                if (Literal(row) is not { } header) continue;
                if (MenuLabels.Key(header) is not { } key) continue;

                if (taken.TryGetValue(key, out var first))
                    clashes.Add($"{menu.File} · {menu.Where}: "
                                + $"'{key}' opens both \"{first}\" and \"{header}\"");
                else
                    taken[key] = header;
            }
        }

        Assert.True(clashes.Count == 0, string.Join("\n  ", clashes.Prepend("")));
    }

    /// <summary>
    /// The other half, and the one that makes the coverage real: a row without
    /// a key has to have been unable to take one.
    /// </summary>
    [Fact]
    public void A_row_goes_without_a_key_only_when_its_first_word_is_spoken_for()
    {
        var lazy = new List<string>();

        foreach (var menu in Menus())
        {
            var taken = menu.Rows
                .Select(Literal)
                .OfType<string>()
                .Select(MenuLabels.Key)
                .OfType<char>()
                .ToHashSet();

            foreach (var row in menu.Rows)
            {
                if (Literal(row) is not { } header) continue;
                if (NeverPickable(row)) continue;
                if (MenuLabels.Key(header) is not null) continue;

                var free = FirstWord(header).FirstOrDefault(letter => !taken.Contains(letter));

                // FirstOrDefault over chars answers '\0' for "there was none",
                // and no header contains one.
                if (free == '\0') continue;

                lazy.Add($"{menu.File} · {menu.Where}: \"{header}\" has no access key "
                         + $"and '{free}' is free");
            }
        }

        Assert.True(lazy.Count == 0, string.Join("\n  ", lazy.Prepend("")));
    }

    /// <summary>
    /// **A second underscore is not a second key, it is an underscore in the
    /// label.** The theme consumes the first marker and draws everything after
    /// it as written — measured, "a_b_c" draws "ab_c" and answers to 'b' alone
    /// — so a stray one does not go unnoticed by the person reading the menu,
    /// it goes unnoticed by whoever typed it. The offence is the underscore
    /// that survives <see cref="MenuLabels.Plain"/>, which catches the doubled
    /// marker "A__B" and the trailing "D_" in the same breath.
    /// </summary>
    [Fact]
    public void No_row_draws_a_stray_underscore()
    {
        var stray = Menus()
            .SelectMany(menu => menu.Rows.Select(row => (menu, header: Literal(row))))
            .Where(x => MenuLabels.Plain(x.header).Contains('_'))
            .Select(x => $"{x.menu.File} · {x.menu.Where}: \"{x.header}\" draws "
                         + $"\"{MenuLabels.Plain(x.header)}\"")
            .ToList();

        Assert.True(stray.Count == 0, string.Join("\n  ", stray.Prepend("")));
    }

    /// <summary>
    /// And a row that can never be picked offers no key: "Installing copyparty…"
    /// is a sentence in the shape of a menu row, and a letter that highlights a
    /// disabled row and does nothing else is worse than no letter at all.
    /// </summary>
    [Fact]
    public void A_row_that_can_never_be_picked_offers_no_key()
    {
        var dead = Menus()
            .SelectMany(menu => menu.Rows.Select(row => (menu, row)))
            .Where(x => NeverPickable(x.row) && MenuLabels.Key(Literal(x.row)) is not null)
            .Select(x => $"{x.menu.File} · {x.menu.Where}: \"{Literal(x.row)}\"")
            .ToList();

        Assert.True(dead.Count == 0, string.Join("\n  ", dead.Prepend("")));

        // A rule with nothing to rule on would pass on an empty file. Six
        // rows: two placeholders in the Share submenu, and then the same pair
        // twice over, once in each column chooser — the Name tick that is on
        // and cannot be turned off, and the sentence under the rule that
        // explains why a ticked column can still be off screen. Twice because
        // there are now two choosers, the header's and Arrange > Columns, and
        // The_two_chooser_menus_offer_the_same_columns requires them to hold
        // the same rows.
        Assert.Equal(6, Menus().SelectMany(m => m.Rows).Count(NeverPickable));
    }

    /// <summary>
    /// **The four rules above all report offenders, so they pass on an empty
    /// list.** A namespace typo, a markup file moved out of RepoSource's walk,
    /// or a menu host this scan does not name would leave every one of them
    /// green while checking nothing. This is the floor under them.
    /// </summary>
    [Fact]
    public void The_scan_reaches_every_menu_and_a_useful_number_of_rows()
    {
        var menus = Menus();

        // Both files that hold menus contribute, and the two windows are
        // reached through the same walk every other markup rule uses.
        Assert.Contains(menus, m => m.File == "MainWindow.axaml");
        Assert.Contains(menus, m => m.File == "SettingsWindow.axaml");

        // Fifteen menus and ninety-eight rows with a literal header, measured
        // today. The floors are written well under those so that adding or
        // dropping a row is not a test failure, and high enough that only a
        // scan which has broken can go under them.
        Assert.True(menus.Count >= 10, $"only {menus.Count} menus were found");

        var rows = menus.SelectMany(m => m.Rows).Select(Literal).OfType<string>().ToList();

        Assert.True(rows.Count >= 70, $"only {rows.Count} rows with a literal header were found");

        // And the listing menu, which is the long one the rules are shaped
        // around, is in there with all of its rows — thirty-nine of them, of
        // which thirty-six carry a literal header.
        var listing = Assert.Single(menus, m => m.Where.Contains("vm:PaneGroupViewModel"));

        Assert.True(listing.Rows.Count >= 35,
                    $"the listing menu came back with {listing.Rows.Count} rows");
    }

    // ---- and the marker means something at runtime --------------------------

    /// <summary>
    /// The measurement the four rules stand on, taken against the theme that
    /// ships — and the only thing that keeps <see cref="MenuLabels"/> honest.
    ///
    /// **The marker is a convention of the CONTROL, not of the string.** A
    /// header of "_Open" is only a mnemonic because the MenuItem template hands
    /// its header to an AccessText with RecognizesAccessKey; hand the same
    /// string to a TextBlock and it draws an underscore. So the rules above,
    /// which read the markup, are worth nothing on their own: what makes them
    /// mean something is that the reader they go through answers exactly what
    /// the realized control does — the same access key, and the same drawn
    /// glyphs — for every shape of header, including the three that look like
    /// they should behave differently and do not.
    ///
    /// This is not a guard. Half of it can only change with an Avalonia
    /// upgrade, but the other half is <see cref="MenuLabels"/>, and any drift
    /// there reddens the row it drifts on.
    ///
    /// Built by hand rather than from MainWindow: the markup carries none of
    /// the awkward shapes — "a_b_c", "D_", "A__B" — precisely because
    /// <see cref="No_row_draws_a_stray_underscore"/> forbids them, so the only
    /// place their behaviour can be established is a menu written here.
    /// </summary>
    [AvaloniaFact]
    public void MenuLabels_reads_a_header_exactly_as_the_theme_draws_it()
    {
        // header, drawn, key — the seven cases, as measured.
        (string Header, string Drawn, string? Key)[] cases =
        [
            ("_Open",      "Open",      "O"),
            ("Cop_y",      "Copy",      "y"),
            ("Open _with", "Open with", "w"),
            ("Refresh",    "Refresh",   null),

            // A doubled underscore reads as an escape and is not one: the theme
            // consumes the first marker and the character it marks is the
            // SECOND underscore, so the row draws the single underscore an
            // escape would have drawn and quietly answers to '_'.
            ("A__B",       "A_B",       "_"),

            // Nothing after it to mark, so it is not a marker: drawn, and no key.
            ("D_",         "D_",        null),

            // Only the FIRST one is notation. The second is drawn as itself and
            // declares nothing — which is the whole of what
            // No_row_draws_a_stray_underscore is protecting.
            ("a_b_c",      "ab_c",      "b"),
        ];

        var menu = new ContextMenu();

        foreach (var (header, _, _) in cases) menu.Items.Add(new MenuItem { Header = header });

        var target = new Border { Width = 200, Height = 100, ContextMenu = menu };
        var window = new Window { Width = 400, Height = 300, Content = target };

        window.Show();
        window.Measure(new Size(400, 300));
        window.Arrange(new Rect(0, 0, 400, 300));

        menu.Open(target);

        window.Measure(new Size(400, 300));
        window.Arrange(new Rect(0, 0, 400, 300));

        var realized = menu.GetVisualDescendants()
            .OfType<AccessText>()
            .ToDictionary(text => text.Text ?? "");

        foreach (var (header, drawn, key) in cases)
        {
            var text = Assert.Contains(header, (IDictionary<string, AccessText>)realized);

            // What the layout actually put on screen, rather than what was
            // handed to it — the marker is taken out at draw time.
            var glyphs = string.Concat(
                text.TextLayout.TextLines.SelectMany(line => line.TextRuns)
                    .Select(run => run.Text.ToString()));

            Assert.Equal(drawn, glyphs);
            Assert.Equal(drawn, MenuLabels.Plain(header));

            // Compared as text: AccessKey is a nullable char, and every
            // Assert.Equal overload xunit offers for one resolves to the pair
            // of sequences instead.
            Assert.Equal(key, text.AccessKey?.ToString());
            Assert.Equal(key?.ToLowerInvariant(), MenuLabels.Key(header)?.ToString());
        }

        menu.Close();
        window.Close();
    }

    /// <summary>
    /// GUARD. Nothing in this repository can turn this red either: it drives
    /// Avalonia's own AccessKeyHandler over a menu built here, and only an
    /// Avalonia upgrade can change the answer. It is the measurement under
    /// <see cref="No_two_rows_in_one_menu_answer_to_the_same_key"/> — the
    /// reason that rule exists rather than the softer "every row gets a key,
    /// clashes and all".
    ///
    /// **A key two rows share moves the highlight; a key one row holds picks
    /// it.** Measured on Avalonia 12.1: with "_Open" and "_Other" both keyed
    /// 'o', four presses walk the focus Open → Other → Open → Other with no
    /// Click raised and the menu still open, while one press of 'y' against
    /// the single "Cop_y" raises its Click and closes the menu.
    ///
    /// WHY IT REACHES FOR A PRIVATE METHOD, which is not a thing to do lightly:
    /// the keystroke cannot be delivered from outside headless. An open
    /// ContextMenu's visual root is a TopLevelHost, which is not an IInputRoot,
    /// so window.KeyPress never reaches the popup; a Menu in the window's own
    /// tree does not answer Alt+letter either, because a headless window is
    /// never activated and the handler will not act for an owner it does not
    /// believe has focus. Both were tried and both did nothing at all. What is
    /// reachable is the decision itself — AccessKeyHandler.ProcessKey, given
    /// the key and the element the press came from — and that decision, not the
    /// plumbing that carries the key to it, is the mechanism the rule is
    /// written around.
    ///
    /// So this fails on an Avalonia upgrade that renames ProcessKey, and it
    /// should: the sentence above would then be describing a method that is
    /// gone.
    /// </summary>
    [AvaloniaFact]
    public void A_key_two_rows_share_moves_the_highlight_rather_than_picking()
    {
        var clicked = new List<string>();

        var open = new MenuItem { Header = "_Open" };
        var other = new MenuItem { Header = "_Other" };
        var copy = new MenuItem { Header = "Cop_y" };

        open.Click += (_, _) => clicked.Add("Open");
        other.Click += (_, _) => clicked.Add("Other");
        copy.Click += (_, _) => clicked.Add("Copy");

        var menu = new ContextMenu();

        menu.Items.Add(open);
        menu.Items.Add(other);
        menu.Items.Add(copy);

        var target = new Border { Width = 200, Height = 100, ContextMenu = menu };
        var window = new Window { Width = 400, Height = 300, Content = target };

        window.Show();
        window.Measure(new Size(400, 300));
        window.Arrange(new Rect(0, 0, 400, 300));

        menu.Open(target);

        window.Measure(new Size(400, 300));
        window.Arrange(new Rect(0, 0, 400, 300));
        Dispatcher.UIThread.RunJobs();

        var handler = typeof(Window)
            .GetProperty("AccessKeyHandler",
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            !.GetValue(window)!;

        var processKey = handler.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == "ProcessKey"
                         && m.GetParameters() is [{ ParameterType.Name: "String" },
                                                  { ParameterType: var second }]
                         && second == typeof(IInputElement));

        string Press(string key)
        {
            var sender = window.FocusManager?.GetFocusedElement() ?? window;

            processKey.Invoke(handler, [key, sender]);

            Dispatcher.UIThread.RunJobs();

            return window.FocusManager?.GetFocusedElement() is MenuItem row
                ? (string)row.Header!
                : "nothing";
        }

        // Two rows, one key: the highlight walks and no row is ever chosen.
        Assert.Equal("_Open", Press("O"));
        Assert.Equal("_Other", Press("O"));
        Assert.Equal("_Open", Press("O"));
        Assert.Equal("_Other", Press("O"));

        Assert.Empty(clicked);
        Assert.True(menu.IsOpen, "the menu closed, so the presses did something else");

        // One row, one key: chosen on the first press, and the menu is done.
        Press("Y");

        Assert.Equal(["Copy"], clicked);
        Assert.False(menu.IsOpen, "picking a row did not close the menu");

        window.Close();
    }
}
