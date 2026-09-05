using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Vaktari.Core;

namespace Vaktari.Ui;

/// <summary>
/// Turns a desktop palette into Avalonia resources.
///
/// Every colour in the markup is a DynamicResource pointing at one of these, so
/// adopting the system scheme is a lookup-table swap rather than a sweep
/// through the XAML — and the accessibility rules survive it, because none of
/// them ever depended on a particular hue. Selection is still marked by an edge
/// bar, and file age is still a lightness ramp.
/// </summary>
public static class ThemeApplier
{
    /// <summary>Our own scheme, used when no desktop theme can be read.</summary>
    private static readonly (string Key, string Dark, string Light)[] Fallback =
    [
        ("AppBackground",       "#181818", "#F0F0F0"),
        ("AppText",             "#E8E8E8", "#1C1C1C"),
        ("ViewBackground",      "#0F0F0F", "#FFFFFF"),
        ("ViewAlternate",       "#151515", "#F7F7F7"),
        ("ViewText",            "#E8E8E8", "#1C1C1C"),
        ("ViewDimText",         "#8A8A8A", "#6A6A6A"),
        ("SelectionBackground", "#1E3A4C", "#CFE6F5"),
        ("SelectionText",       "#FFFFFF", "#101010"),
        ("AccentColour",        "#56B4E9", "#0072B2"),
        ("BorderColour",        "#242424", "#D4D4D4"),
        ("SeparatorColour",     "#242424", "#DCDCDC"),
        ("DividerColour",       "#303030", "#C8C8C8"),
        ("PanelBackground",     "#1B1B1B", "#EDEDED"),
        ("HoverBackground",     "#1A2A34", "#E8F1F8"),
        ("EdgeHighlight",       "#16FFFFFF", "#96FFFFFF"),
        ("EdgeShadow",          "#5A000000", "#28000000"),
        ("ChipBackground",      "#22FFFFFF", "#18000000"),
    ];

    /// <summary>
    /// The design reference's palette, in both lightnesses.
    ///
    /// **The light column is not the dark one inverted, and the surfaces are
    /// where that shows.** In the dark scheme the listing is the DARKEST of the
    /// three surfaces and the chrome the lightest, so the content recedes and
    /// the frame sits in front of it. Invert the numbers and you get a listing
    /// darker than the window around it, which reads as a hole. A light
    /// interface wants the opposite arrangement — the listing is paper, the
    /// lightest thing on screen, with the sidebar and chrome stepping down away
    /// from it. So the three-surface STRUCTURE is preserved and their ORDER is
    /// reversed, which is the only way both schemes end up looking deliberate.
    ///
    /// The neutrals keep their violet bias in both columns rather than becoming
    /// plain grey; that bias is what makes them read as chosen, and dropping it
    /// on the light side would leave the accent looking bolted on.
    ///
    /// **Checked, not eyeballed.** Every text-on-surface pair here clears WCAG
    /// AA, worst case 5.12:1 — the accent on chrome, which is the pair the dark
    /// column only reaches 3.41:1 on. The light column is the stronger of the
    /// two and was not weakened to match.
    /// </summary>
    private static readonly (string Key, string Dark, string Light)[] Design =
    [
        // Surfaces. Dark, from the mock's own markup: window and chrome
        // #2b2b32, sidebar #26262d, listing and the active tab #23232b.
        ("AppBackground",   "#2b2b32", "#e8e8ef"),
        ("ChromeBrush",     "#2b2b32", "#e8e8ef"),
        ("PanelBackground", "#26262d", "#eff0f5"),
        ("ViewBackground",  "#23232b", "#fcfcfe"),
        ("ViewAlternate",   "#26262d", "#f4f4f8"),

        ("WindowText",      "#e7e7ec", "#1e1e26"),
        ("ViewText",        "#e7e7ec", "#1e1e26"),

        // Dark: #8b8b95 measured 4.45:1 against PanelBackground — five
        // hundredths under WCAG AA for body text, and this role carries the
        // sidebar's group headings and drive sizes. #909099 is 4.75:1 and is
        // not a colour anybody can tell apart from the old one. Against
        // ViewBackground the original already passed at 4.62:1, so that was the
        // panel case only. Light: #5f5f6d clears every surface by a wider
        // margin, because on a pale ground there is room to.
        // Raised again, for the surface the last pass did not check. #909099
        // clears AA on the listing and the panel and measures 4.44:1 on the
        // window chrome — five hundredths under, on the surface that carries
        // the column headers, the status bar, the inactive tab titles and the
        // breadcrumb ancestors. #9a9aa3 clears all four and is not a colour
        // anybody can tell apart from the old one.
        ("ViewDimText",     "#9a9aa3", "#5f5f6d"),

        ("SeparatorColour", "#34343c", "#d5d5e0"),
        ("BorderColour",    "#34343c", "#d5d5e0"),

        // **The accent has to darken for the light column.** #6d6df0 is a
        // mid-violet: it sits 3.78:1 on the dark listing, which is thin but
        // legible, and only 2.9:1 on a white one, which is not. #4f4fd0 is the
        // same hue walked down until it clears AA on all three light surfaces.
        // The dark column keeps the mock's value.
        ("AccentColour",    "#6d6df0", "#4f4fd0"),

        // AccentText is NOT here. It is derived from whatever AccentColour ends
        // up being — see ReadableAccent — because the accent has three possible
        // sources and a hand-written second value would only have covered one
        // of them.

        // The checked segment is rgba(109,109,240,.22) in the mock, which is
        // the tint AccentDim is bound to everywhere it is used. Same 22% on the
        // light side, over a pale ground instead of a dark one.
        ("AccentDim",       "#386d6df0", "#384f4fd0"),

        ("ChipBackground",  "#31313a", "#e2e2ec"),

        // **Two windows asked for this and nothing defined it**, so the theme
        // cards in settings and the conflict dialog's panel drew with no
        // background at all — whatever was behind them showed through. It is
        // the listing's alternating row shade, which is what both wanted.
        ("ViewAlternateRow", "#26262d", "#f4f4f8"),

        // **Hover flips from a white wash to a black one.** A translucent white
        // over a pale surface is very nearly nothing; the point of the wash is
        // that it works whatever is underneath, and on light that means going
        // down rather than up.
        ("HoverBackground", "#14ffffff", "#12000000"),

        // Selection is the accent at 30% either way. The TEXT on it cannot be
        // the same colour in both: near-white over a pale lavender fill is the
        // one place a straight inversion would have produced something
        // genuinely unreadable.
        ("SelectionBackground", "#4d6d6df0", "#3d4f4fd0"),
        ("SelectionText",       "#e7e7ec",   "#14141c"),
    ];

    private static readonly (string Resource, string Role)[] Mapping =
    [
        ("AppBackground",       ThemeRole.WindowBackground),
        ("AppText",             ThemeRole.WindowText),
        ("ViewBackground",      ThemeRole.ViewBackground),
        ("ViewAlternate",       ThemeRole.ViewAlternate),
        ("ViewText",            ThemeRole.ViewText),
        ("ViewDimText",         ThemeRole.ViewDimText),
        ("SelectionBackground", ThemeRole.SelectionBackground),
        ("SelectionText",       ThemeRole.SelectionText),
        ("AccentColour",        ThemeRole.Accent),

    ];

    public static void Apply(Window window, ThemePalette? palette)
    {
        // Application-scoped so every window — including properties — resolves
        // the same palette. Window-scoped resources are invisible to siblings.
        var target = Application.Current?.Resources ?? window.Resources;

        // **One variable decides lightness, and everything else reads it.** The
        // setting overrules the desktop when it says anything but FollowDesktop;
        // the desktop is the default and the fallback, including when no palette
        // could be read at all. Deliberately computed here rather than at the
        // three places that consume it — the whole failure this file was
        // repaired for was two things deciding lightness independently.
        var dark = Settings.AppSettings.Current.Views.ThemeMode switch
        {
            Core.Settings.ThemeMode.Light => false,
            Core.Settings.ThemeMode.Dark => true,
            _ => palette?.IsDark ?? true,
        };

        // **Fluent has to be told, or it answers this question separately and
        // differently.**
        //
        // Not every colour on screen comes from the table below. A ListBoxItem's
        // foreground — which is what a filename in the listing actually inherits,
        // since that TextBlock sets no Foreground of its own — comes from
        // FluentTheme, and FluentTheme picks its own values from the requested
        // theme variant. App.axaml asks for Default, which follows the OS.
        //
        // So the palette said one thing and Fluent said another, and nothing
        // reconciled them. On a machine set to LIGHT, the shipping build painted
        // the design scheme's dark surfaces and then let Fluent write nearly
        // black filenames onto them: measured 1.02:1, which is not "hard to
        // read", it is invisible. It went unnoticed because the machine it was
        // built on is set to dark, where the two happen to agree.
        //
        // One decision, applied to both. Whatever picks `dark` above now picks
        // the variant too, so they cannot drift apart again.
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = dark
                ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Light;
        }

        // A sane value for every role, so nothing downstream is ever unset.
        foreach (var (key, darkValue, lightValue) in Fallback)
            target[key] = Brush(dark ? darkValue : lightValue);

        // **The reference scheme is the BASE now, not the last word.**
        //
        // It used to run last and overwrite everything: of the seventeen
        // resources derived from the desktop palette below, thirteen were
        // replaced outright and the four survivors are used by no markup in the
        // window. So this file read the desktop's colours, spent a hundred lines
        // deriving separators, bands, bevels and an age ramp from them, and threw
        // all of it away — leaving IThemeProvider, KdeThemeProvider and
        // WindowsThemeProvider, some five hundred lines between them, delivering
        // exactly one live value: palette.SingleClick.
        //
        // Applying it first inverts that without changing how anything looks.
        // The default is unchanged, because the default is still this scheme.
        // What changes is that the desktop can now be layered ON TOP when asked,
        // which is what all that derivation was written for.
        ApplyDesignScheme(target, dark);

        // The desktop gets a say only when the user asks for one. Off by
        // default: the scheme above is a considered look, and a file manager
        // that repaints itself to match Plasma the first time it is launched is
        // a surprise, not a feature.
        if (!Settings.AppSettings.Current.Views.FollowDesktopColours || palette is null)
        {
            Finish(target, palette, dark);
            return;
        }

        foreach (var (resource, role) in Mapping)
        {
            if (palette.Colours.TryGetValue(role, out var hex) && Brush(hex) is { } brush)
                target[resource] = brush;
        }

        if (target["AccentColour"] is ISolidColorBrush accent)
        {
            // A dimmed accent for selection fills. No desktop exposes "the
            // accent at 25%", and a flat accent behind text is far too loud for
            // a whole row.
            target["AccentDim"] = new SolidColorBrush(
                Color.FromArgb(dark ? (byte)70 : (byte)55,
                    accent.Color.R, accent.Color.G, accent.Color.B));

            target["HoverBackground"] = new SolidColorBrush(
                Color.FromArgb(dark ? (byte)28 : (byte)24,
                    accent.Color.R, accent.Color.G, accent.Color.B));
        }

        // Separators are DERIVED from the background, not taken from a text
        // role. ForegroundInactive is a foreground colour — using it draws hard
        // grey rules through the window, which is nothing like how Breeze
        // separates regions. Blending the background a little way toward the
        // text gives a line that reads as a seam at any scheme lightness.
        if (target["AppBackground"] is ISolidColorBrush back &&
            target["AppText"] is ISolidColorBrush fore)
        {
            target["SeparatorColour"] = new SolidColorBrush(
                Blend(back.Color, fore.Color, dark ? 0.10 : 0.14));

            // A slightly stronger version for the one edge that has to read as
            // a real boundary: the split divider.
            target["DividerColour"] = new SolidColorBrush(
                Blend(back.Color, fore.Color, dark ? 0.18 : 0.22));

            target["PanelBackground"] = new SolidColorBrush(
                dark ? Darken(back.Color, 0.22) : Darken(back.Color, 0.05));

            // Bevels. Kept in the table because nothing else derives from them
            // and a future bevel may want them, but **no longer used as region
            // borders** — every band boundary is a SeparatorColour hairline now.
            // With both present each band had two edges, a light one from here
            // and a dark one from EdgeShadow, which is one more than a boundary
            // needs.
            target["EdgeHighlight"] = new SolidColorBrush(
                Color.FromArgb(dark ? (byte)22 : (byte)150, 255, 255, 255));

            target["EdgeShadow"] = new SolidColorBrush(
                Color.FromArgb(dark ? (byte)90 : (byte)40, 0, 0, 0));

            // Flat, where this used to be a two-stop vertical gradient. The
            // gradient and the bevel pair were doing the same job as the seams,
            // and a flat fill survives an arbitrary desktop scheme better than a
            // derived ±5% ramp does — on a scheme already near black or white
            // the ramp clips and the "surface" reads as a smudge.
            target["ChromeBrush"] = new SolidColorBrush(back.Color);
        }

        // A chip background that works on both light and dark: a wash of the
        // view text colour rather than a fixed translucent white, which is
        // invisible on a pale scheme. Named for the tag chips it was written
        // for; those are gone, and the toolbar still uses it.
        if (target["ViewText"] is ISolidColorBrush chipText)
        {
            target["ChipBackground"] = new SolidColorBrush(
                Color.FromArgb(dark ? (byte)26 : (byte)20,
                    chipText.Color.R, chipText.Color.G, chipText.Color.B));
        }

        Finish(target, palette, dark);
    }

    /// <summary>
    /// The parts that must run whichever scheme won, in the order they depend
    /// on each other.
    ///
    /// Both paths through <see cref="Apply"/> end here, which is the point: the
    /// age ramp has to be derived from the colours that FINALLY landed, and the
    /// trace has to report the font that finally landed. Getting either from a
    /// value written earlier is exactly the bug that let a broken font setting
    /// ship — the log named the chosen font while the window rendered another.
    /// </summary>
    /// <summary>
    /// The accent, walked toward the text colour until it can be read on the
    /// surfaces it is used over.
    ///
    /// Toward WindowText rather than simply lightened: on a dark theme that is
    /// up and on a light one it is down, so one rule serves both without
    /// asking which way "readable" points. The hue survives because the blend
    /// is short — a few steps is enough for the cases that fail — and an accent
    /// that already clears AA is returned untouched, which is what happens on
    /// every light scheme here.
    ///
    /// Capped at twelve steps so a pathological desktop accent ends up as
    /// something legible rather than looping; by then it is close enough to the
    /// text colour to read regardless.
    /// </summary>
    private static Color ReadableAccent(Color accent, IResourceDictionary target, bool dark)
    {
        var grounds = new[] { "ChromeBrush", "ViewBackground", "AppBackground", "PanelBackground" }
            .Select(key => target.TryGetResource(key, null, out var value)
                           && value is ISolidColorBrush brush
                ? brush.Color
                : (Color?)null)
            .OfType<Color>()
            .ToList();

        if (grounds.Count == 0) return accent;

        var toward = target.TryGetResource("ViewText", null, out var textValue)
                     && textValue is ISolidColorBrush text
            ? text.Color
            : dark ? Colors.White : Colors.Black;

        var candidate = accent;

        for (var step = 0; step < 12; step++)
        {
            if (grounds.All(ground => Contrast(candidate, ground) >= 4.5)) break;

            candidate = Blend(candidate, toward, 0.12);
        }

        return candidate;
    }

    /// <summary>WCAG relative luminance, and the ratio built from it.</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        var (hi, lo) = x > y ? (x, y) : (y, x);

        return (hi + 0.05) / (lo + 0.05);
    }

    private static void Finish(IResourceDictionary target, ThemePalette? palette, bool dark)
    {
        // **The accent does two jobs and only one of them is read.** As a fill
        // — the selected row's edge bar, the settings tab marker — contrast
        // against the ground is a matter of taste. As TEXT it is a requirement,
        // and the accent failed it: #6d6df0 measures 3.41:1 on the chrome and
        // 3.78:1 on the listing, both under AA's 4.5:1 at the 11-12.5px these
        // labels are actually set in, in the theme that is the default.
        //
        // Derived rather than written down beside the others, because the
        // accent has three sources — the design scheme, the fallback scheme,
        // and whatever the desktop hands over — and a second literal would have
        // fixed exactly one of them. A desktop accent is somebody else's colour
        // and can be anything at all.
        //
        // Here rather than beside the other accent-derived brushes, because
        // those sit in the branch that only runs when a desktop palette is
        // being followed — and AccentText has to exist on every path or the
        // labels bound to it render with no colour at all.
        if (target["AccentColour"] is ISolidColorBrush accentText)
            target["AccentText"] = new SolidColorBrush(
                ReadableAccent(accentText.Color, target, palette?.IsDark ?? true));

        // The file-age ramp is derived rather than hardcoded: fixed pale blues
        // disappear on a light scheme. Fresh files get full text colour,
        // ancient ones fade past the dim colour — a lightness ramp that holds
        // under any scheme, this one or a desktop's.
        ApplyBanding(target, dark);
        ApplyAgeRamp(target);

        // Always set, so the markup can bind unconditionally. A configured font
        // wins over the desktop's, which is the whole point of configuring one;
        // blank means follow Plasma, which stays the default.
        // Published here rather than at each call site: Apply is the one place
        // every palette read funnels through — startup, a Plasma change, and a
        // settings save all reach it — so this cannot fall out of step.
        MainWindow.SystemSingleClick = palette?.SingleClick;

        // The desktop's text SIZE, published the same way and for the same
        // reason — it arrives on the same palette, in the same read.
        //
        // **Both fields are read here because neither desktop states both.**
        // Plasma puts a point size in kdeglobals and Windows puts a percentage
        // under Accessibility; a UI layer that read only one of them would
        // follow the text size on exactly one platform, and ThemePalette.FontSize
        // is how it managed to be read on neither — parsed out of kdeglobals
        // since the provider was written, carried on the record, and never once
        // looked at.
        InterfaceText.SystemScale = InterfaceText.FromPalette(palette);

        // Precedence, most specific first. ApplyDesignScheme has already put the
        // reference typeface in, so the last arm is "leave it alone" rather than
        // a value — which is why this reads as two overrides and not a chain.
        var chosen = Settings.AppSettings.Current.Views.CustomFontFamily;

        if (chosen is { Length: > 0 })
        {
            target["AppFontFamily"] = new FontFamily(chosen);
        }
        else if (Settings.AppSettings.Current.Views.FollowDesktopColours
                 && palette?.FontFamily is { Length: > 0 } family)
        {
            target["AppFontFamily"] = new FontFamily(family);
        }

        // Logged from the dictionary, after everything that writes to it. This
        // line used to run before the design scheme and report the value it had
        // just written — which was then replaced — so it printed
        // applied='Segoe UI' while the window was unmistakably in JetBrains
        // Mono. A trace added to make a font problem visible spent its life
        // describing a value nothing ever rendered.
        //
        // A diagnostic that reports an intention rather than an outcome is
        // worse than none: it answers the question convincingly and wrongly.
        Console.Error.WriteLine(
            $"[vaktari] font: configured='{chosen ?? "(none)"}' "
            + $"desktop='{palette?.FontFamily ?? "(none)"}' "
            + $"applied='{target["AppFontFamily"]}'");
    }

    /// <summary>
    /// **The design reference's own palette and typeface, applied verbatim,
    /// last, over everything the desktop said.**
    ///
    /// This is a deliberate reversal of the rule the rest of this file exists
    /// to enforce. Everything above derives its colours from the desktop scheme
    /// so the window looks like part of it; the handoff says the mock's hex
    /// values are "reference values only" for exactly that reason. Requested
    /// anyway, and requested twice: a 1:1 match with
    /// `Vaktari Window.dc.html`, which cannot be had while the desktop still
    /// gets a vote.
    ///
    /// **What this costs**, so it is not discovered later: the window no longer
    /// follows the desktop's colour scheme, accent or font. It does follow the
    /// desktop's light/dark preference — see <see cref="Design"/> — because that
    /// is a different question from which hues to use, and answering it wrongly
    /// means a pitch-black window on a machine set to light.
    ///
    /// **To revert**, delete the call above. Nothing else references this.
    /// </summary>
    private static void ApplyDesignScheme(IResourceDictionary target, bool dark)
    {
        static SolidColorBrush B(string hex) => new(Color.Parse(hex));

        foreach (var (key, darkValue, lightValue) in Design)
            target[key] = B(dark ? darkValue : lightValue);

        // **Two faces, split by what the text is for.**
        //
        // Monospace everywhere was the reference, and it cost more than it
        // looked. Every glyph is set to the width of the widest one, so a
        // proportional face fits roughly 15-20% more label in the same space —
        // measured directly here when a test set Georgia and "OS Disk (C:)" fit
        // where "OS Disk (…" had been truncating. Three separate clipping faults
        // this week traced back to labels needing more room than they had.
        //
        // So: proportional for anything READ — filenames, sidebar labels, the
        // breadcrumb, menus. Monospace kept for anything COMPARED DOWN A COLUMN
        // — sizes, dates, permissions, hashes — where equal advance widths are
        // the entire point and digits have to stack. That is the job monospace
        // exists for, and it keeps the character of the design exactly where it
        // earns its keep.
        //
        // Segoe UI Variable is Windows 11's own text face and reads a little
        // narrower than plain Segoe UI at small sizes; the rest of the stack is
        // the ordinary fallback chain, ending at whatever the system calls its
        // UI font.
        target["AppFontFamily"] =
            new FontFamily("Segoe UI Variable Text, Segoe UI, Inter, Cantarell, Noto Sans, sans-serif");

        // The reference typeface, still here and still doing a job. Bound by the
        // size and modified columns, so a listing keeps its digits aligned.
        // Deliberately NOT affected by the font setting: choosing a face for the
        // interface is a preference, whereas a column of numbers that no longer
        // lines up is a defect.
        //
        // **Skipped when the user picked a font, and that exception is the
        // reason this method takes an argument it otherwise would not need.**
        // Everything else here deliberately overrides the desktop — that is
        // what applying the reference verbatim means, and the desktop's colours
        // are a default rather than a decision. A font chosen in Settings is
        // not a default. It was being computed, logged, and then overwritten
        // three lines later, so the setting appeared to do nothing at all: the
        // list offered every installed family, accepted the choice, saved it,
        // and the window carried on in JetBrains Mono.
        //
        // The ordering that makes the rest of this correct is exactly what made
        // the font wrong, which is why it reads as an exception rather than a
        // reordering. Moving the whole block earlier would hand the desktop's
        // palette back its win over the reference.
        target["AppMonoFamily"] =
            new FontFamily("JetBrainsMono NF, JetBrains Mono, Cascadia Mono, Consolas");

        // No ramp here. Finish runs after this on BOTH paths and builds one
        // from whatever the text colours finally are, so a second copy here
        // could only ever be overwritten — which is what the two of them were
        // doing, with the same bug in each.
    }

    private static Color Lighten(Color c, double amount) => Blend(c, Colors.White, amount);
    private static Color Darken(Color c, double amount) => Blend(c, Colors.Black, amount);

    /// <summary>
    /// Keeps the row banding far enough from the listing to be seen.
    ///
    /// **This ran only when the desktop was being followed**, which is off by
    /// default — so the scheme almost everybody sees banded at 1.04:1 and the
    /// guard written to prevent exactly that never looked at it. It belongs
    /// here, after both paths have settled, where the value it judges is the
    /// one that will be on screen.
    ///
    /// The desktop's own value is kept whenever it is far enough to read;
    /// Breeze Dark's sits a couple of values from its view colour, which
    /// vanishes on a large monitor.
    /// </summary>
    private static void ApplyBanding(IResourceDictionary target, bool dark)
    {
        if (target["ViewBackground"] is not ISolidColorBrush view) return;

        var alt = (target["ViewAlternate"] as ISolidColorBrush)?.Color ?? view.Color;

        if (Contrast(alt, view.Color) < BandContrast)
            target["ViewAlternate"] = new SolidColorBrush(BandFor(view.Color, dark));
    }

    /// <summary>
    /// How far a banded row has to sit from the one above it. Row banding is a
    /// reading aid rather than text, so this is well below AA on purpose --
    /// stripes you notice are worse than no stripes. It only has to be a
    /// difference the eye can find when it follows a row across a wide window.
    /// </summary>
    private const double BandContrast = 1.2;

    /// <summary>
    /// A band that far from the ground, whatever the ground is. Stepped rather
    /// than a fixed blend because a fixed one is exactly what failed: 4.5%
    /// toward white is a visible step from mid-grey and nothing at all from
    /// near-black.
    /// </summary>
    private static Color BandFor(Color view, bool dark)
    {
        var band = view;

        for (var amount = 0.02; amount <= 0.30; amount += 0.01)
        {
            band = dark ? Lighten(view, amount) : Darken(view, amount);

            if (Contrast(band, view) >= BandContrast) break;
        }

        return band;
    }

    /// <summary>
    /// The six age shades, freshest first.
    ///
    /// **The two oldest were the same colour.** Six stops were spread over a
    /// range of 1.25 and then clamped back to 1.0, so the fifth and the sixth
    /// both landed exactly on the dim colour -- a year old and a decade old
    /// drawn identically, and the ramp was really five shades wearing six
    /// names. The intent behind the 1.25 was that "ancient" should recede
    /// FURTHER than ordinary secondary text, which is what the last stop now
    /// does by carrying on toward the background instead of stopping dead at
    /// dim.
    ///
    /// One builder, because there were two of these -- the same arithmetic
    /// written out twice, in two methods, with the same bug in both.
    /// </summary>
    private static void ApplyAgeRamp(IResourceDictionary target)
    {
        if (target["ViewText"] is not ISolidColorBrush text
            || target["ViewDimText"] is not ISolidColorBrush dim
            || target["ViewBackground"] is not ISolidColorBrush view) return;

        var ramp = new IBrush[6];

        // Five even stops from ordinary text down to the dim colour, which is
        // what the old arithmetic worked out to for its first five.
        for (var i = 0; i < 5; i++)
            ramp[i] = new SolidColorBrush(Blend(text.Color, dim.Color, i / 4.0));

        ramp[5] = new SolidColorBrush(Blend(dim.Color, view.Color, 0.35));

        ViewModels.AgeConverters.SetRamp(ramp);
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    private static IBrush? Brush(string hex)
    {
        try { return new SolidColorBrush(Color.Parse(hex)); }
        catch { return null; }
    }
}
