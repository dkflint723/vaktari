using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.Thumbnails;

/// <summary>
/// Turns a themed icon file into something drawable.
///
/// PNGs decode normally. SVGs go through a deliberately small subset renderer:
/// Avalonia's Geometry.Parse already understands SVG path data, and it has real
/// gradient brushes, so shapes, flat fills and gradients need no SVG library.
/// Anything genuinely beyond that — filters, masks, clip paths, text, embedded
/// rasters — is declined rather than approximated, and the caller falls back to
/// the drawn glyph. A wrong icon is worse than a generic one.
/// </summary>
public static class IconLoader
{
    private const int MaxResolved = 4000;

    // Drawn holds a rendered drawable per icon FILE, so it is bounded in
    // practice by how many distinct icons a theme resolves to — but nothing
    // enforced that, while its sibling Resolved was capped. Same treatment for
    // both rather than one bounded cache and one that is merely finite.
    private const int MaxDrawn = 2000;

    private static readonly ConcurrentDictionary<string, string?> Resolved = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IImage?> Drawn = new(StringComparer.Ordinal);

    public static IIconThemeProvider? Provider { get; set; }

    /// <summary>
    /// Paint we cannot reproduce faithfully. Gradients are NOT here: icon themes
    /// use them heavily — Tela and Papirus are almost entirely gradient — and
    /// declining them meant declining whole themes.
    /// </summary>
    private static readonly string[] Unsupported =
        ["filter", "mask", "clipPath", "text", "image", "pattern"];

    /// <summary>
    /// Which file represents this entry. Pure filesystem work and safe from any
    /// thread — deliberately separate from <see cref="Load"/>, which is not.
    /// </summary>
    public static string? ResolveFile(string path, bool isDirectory, int size)
    {
        if (Provider is null) return null;

        // Keyed by the icon we are actually asking for, not by the file asking
        // for it. Every .png wants the same icon; so does every ordinary folder
        // — but Documents and Downloads want different ones, and a single
        // "dir" key gave all of them whatever the first folder resolved to.
        IReadOnlyList<string> names;

        try { names = Provider.NamesFor(path, isDirectory); }
        catch { return null; }

        if (names.Count == 0) return null;

        var key = $"{names[0]}|{size}";

        if (Resolved.TryGetValue(key, out var cached)) return cached;

        string? file = null;

        try { file = Provider.Resolve(names, size); }
        catch { /* an unreadable theme means no icon, not a failure */ }

        // Extensionless files fall back to a per-path key because their type
        // depends on content, so bound the dictionary rather than let those
        // accumulate forever.
        if (Resolved.Count > MaxResolved) Resolved.Clear();

        Resolved[key] = file;
        return file;
    }


    /// <summary>
    /// Drops every cached icon. Called when the desktop theme changes: the
    /// resolved paths belong to the old icon theme, and the drawables were
    /// built with the old text colour baked into every currentColor.
    /// </summary>
    public static void Invalidate()
    {
        Resolved.Clear();
        Drawn.Clear();
        Fallbacks.Clear();
        FileTypeIcon.Clear();
    }

    // ---- the desktop's own per-file icons ---------------------------------

    /// <summary>
    /// The platform's per-file icons, where it has such a thing. Separate from
    /// <see cref="Provider"/>, which answers by icon NAME and gives every text
    /// file the same picture; this answers per file, so an executable shows its
    /// own icon and a shortcut carries its overlay.
    /// </summary>
    public static IFileIconProvider? Files { get; set; }

    /// <summary>Whether to use them, which is the user's choice and off by
    /// default — the bundled set is the one this application looks right in.</summary>
    /// <summary>
    /// **An imported theme wins over the desktop's own icons.** Both can be set
    /// — a checkbox and a folder are independent controls — and the theme is
    /// the more deliberate choice of the two, so it is the one honoured rather
    /// than whichever happens to be checked last.
    /// </summary>
    public static bool UseSystemIcons =>
        Files is not null
        && Provider is null
        && Settings.AppSettings.Current.General.UseSystemIcons;

    /// <summary>
    /// The desktop's pixels for this file. **Off the UI thread** — composing an
    /// icon reads a resource out of some DLL, and this is called once per
    /// visible row. The provider does its own caching, so a folder of four
    /// thousand text files asks the shell once.
    /// </summary>
    public static IconPixels? SystemPixels(string path, bool isDirectory, int size)
    {
        if (Files is not { } files) return null;

        try
        {
            return files.IconFor(path, isDirectory, size);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] system icon failed: {Path.GetFileName(path)} — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pixels into a drawable. **UI thread**, like everything else here that
    /// builds one.
    ///
    /// **Keyed on the pixels themselves, through a weak table.** The provider
    /// hands back the same IconPixels instance for every file that shares an
    /// icon, so this builds one bitmap per distinct icon rather than one per
    /// row — and because the table holds its keys weakly, an icon the provider
    /// drops takes its bitmap with it instead of pinning it forever. Nothing
    /// here has to know or copy the provider's keying rule.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IconPixels, IImage>
        SystemDrawn = new();

    public static IImage? Draw(IconPixels pixels)
    {
        if (SystemDrawn.TryGetValue(pixels, out var cached)) return cached;

        try
        {
            var image = ToBitmap(pixels);
            SystemDrawn.AddOrUpdate(pixels, image);
            return image;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] system icon draw failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Raw BGRA into something Avalonia can draw.
    ///
    /// **Row by row, using the buffer's own stride.** A locked framebuffer is
    /// padded to an alignment that is usually but not always width × 4, and a
    /// single block copy against a padded buffer produces an image that shears
    /// diagonally — which reads as a corrupt icon rather than as a stride bug.
    ///
    /// **Internal, and a WriteableBitmap rather than an IImage, because
    /// thumbnails need it too.** The Windows shell hands back a thumbnail the
    /// same way it hands back an icon — pixels, no file — so
    /// <see cref="ThumbnailLoader"/> has the identical conversion to do and the
    /// stride lesson above is not worth learning twice. It wants a Bitmap
    /// specifically, which is what this builds.
    /// </summary>
    internal static WriteableBitmap ToBitmap(IconPixels pixels)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(pixels.Width, pixels.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var buffer = bitmap.Lock();

        var stride = pixels.Width * 4;

        for (var y = 0; y < pixels.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                pixels.Bgra,
                y * stride,
                buffer.Address + y * buffer.RowBytes,
                stride);
        }

        return bitmap;
    }

    /// <summary>
    /// Builds the drawable. **UI thread only.** Everything here creates Avalonia
    /// objects — DrawingImage, GeometryDrawing, brushes, GradientStops — and
    /// CurrentColour reads Application.Current.Resources. Doing this on a pool
    /// thread is what crashed the process: thumbnails get away with Task.Run
    /// because Bitmap is a plain object, and none of these are.
    /// </summary>
    public static IImage? Load(string file)
    {
        if (Drawn.TryGetValue(file, out var cached)) return cached;

        IImage? image = null;

        try
        {
            image = file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? LoadSvg(file)
                : new Bitmap(file);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] icon load failed: {Path.GetFileName(file)} — {ex.Message}");
        }

        if (Drawn.Count > MaxDrawn) Drawn.Clear();

        Drawn[file] = image;
        return image;
    }

    private static IImage? LoadSvg(string file)
    {
        var root = XDocument.Load(file).Root;
        if (root is null) return null;

        foreach (var name in Unsupported)
        {
            if (!root.Descendants().Any(e => e.Name.LocalName == name)) continue;

            Console.Error.WriteLine($"[vaktari] icon declined ({name}): {Path.GetFileName(file)}");
            return null;
        }

        var bounds = ReadViewBox(root);
        var gradients = ReadGradients(root, bounds);
        var styles = ReadStyles(root);
        var group = new DrawingGroup();

        Walk(root, group, inheritedFill: null, gradients, styles);

        if (group.Children.Count == 0) return null;

        // NOTE: in Avalonia, DrawingGroup.ClipGeometry does NOT affect GetBounds
        // (AvaloniaUI/Avalonia#18512 — deliberately different from WPF), and
        // DrawingImage.Size is exactly Drawing.GetBounds().Size. So a clip can
        // never correct a bad size; it only hides pixels. Sizing therefore has
        // to come from the geometry we actually add.
        var ink = group.GetBounds();

        if (Diagnose)
        {
            Console.Error.WriteLine(
                $"[vaktari] icon {Path.GetFileName(file)}: viewBox {bounds}, ink {ink}");

            foreach (var child in group.Children)
            {
                if (child is not GeometryDrawing shape) continue;

                Console.Error.WriteLine(
                    $"[vaktari]   shape {shape.Geometry?.Bounds} " +
                    $"brush={Describe(shape.Brush)} pen={Describe(shape.Pen?.Brush)}");
            }
        }

        // No clip: it cannot fix the size, and it can crop real artwork when an
        // icon draws outside the viewBox it declares.
        return new DrawingImage { Drawing = group };
    }

    /// <summary>Set VAKTARI_ICON_DEBUG=1 to dump per-shape bounds and paint.</summary>
    private static readonly bool Diagnose =
        Environment.GetEnvironmentVariable("VAKTARI_ICON_DEBUG") == "1";

    private static string Describe(IBrush? brush) => brush switch
    {
        null => "none",
        ISolidColorBrush solid => solid.Color.ToString(),

        // The AXIS and its unit, not just the stop count. Which shape a
        // gradient is painted into, and in what units, is the whole question
        // when a fade lands in the wrong place — and printing "gradient(2
        // stops)" answered none of it. Matched on the concrete types this file
        // constructs, so the interface names cannot be wrong.
        LinearGradientBrush linear =>
            $"linear {Where(linear.StartPoint)}->{Where(linear.EndPoint)} [{Stops(linear)}]",

        RadialGradientBrush radial =>
            $"radial centre {Where(radial.Center)} [{Stops(radial)}]",

        IGradientBrush gradient => $"gradient({gradient.GradientStops.Count} stops)",
        _ => brush.GetType().Name,
    };

    private static string Where(RelativePoint point)
        => $"({point.Point.X:0.##},{point.Point.Y:0.##} "
           + $"{(point.Unit == RelativeUnit.Absolute ? "abs" : "rel")})";

    private static string Stops(IGradientBrush gradient)
        => string.Join(" ", gradient.GradientStops
            .Select(stop => $"{stop.Offset:0.##}:{stop.Color}"));

    // ---- stylesheet ------------------------------------------------------

    /// <summary>
    /// Declarations from &lt;style&gt; blocks, keyed by selector (".cls", "#id",
    /// "tag").
    ///
    /// Icon sets routinely put their colour here rather than on the element —
    /// Tela's folders are a class fill — and without reading it the fill was
    /// unresolvable, so every folder fell back to the text colour and came out
    /// white on a dark scheme.
    ///
    /// A deliberate subset: simple selectors and the two properties that decide
    /// colour. No cascade, no specificity, no media queries.
    /// </summary>
    private static Dictionary<string, (string? Fill, string? Colour, string? Stroke)> ReadStyles(
        XElement root)
    {
        var rules = new Dictionary<string, (string?, string?, string?)>(StringComparer.Ordinal);

        foreach (var block in root.Descendants().Where(e => e.Name.LocalName == "style"))
        {
            var css = block.Value;
            if (css.Length == 0) continue;

            // Comments would otherwise be parsed as declarations.
            css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

            foreach (Match rule in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
            {
                string? fill = null, colour = null, stroke = null;

                foreach (var declaration in rule.Groups[2].Value
                             .Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = declaration.Split(':', 2);
                    if (pair.Length != 2) continue;

                    var value = pair[1].Trim();

                    switch (pair[0].Trim())
                    {
                        case "fill": fill = value; break;
                        case "color": colour = value; break;
                        case "stroke": stroke = value; break;
                    }
                }

                if (fill is null && colour is null && stroke is null) continue;

                foreach (var selector in rule.Groups[1].Value
                             .Split(',', StringSplitOptions.RemoveEmptyEntries))
                    rules[selector.Trim()] = (fill, colour, stroke);
            }
        }

        ApplyColourScheme(rules);

        return rules;
    }

    /// <summary>
    /// KDE's colour-scheme classes, filled in from the LIVE palette — the thing
    /// that makes our folders a different colour from Dolphin's.
    ///
    /// **These icons deliberately declare no colour of their own.** Tela's
    /// `folder.svg` (which `inode-directory.svg` symlinks to) paints with
    /// `fill="currentColor"` on `class="ColorScheme-Highlight"`, and ships
    /// `.ColorScheme-Highlight { color: #5294e2 }` only as a FALLBACK so the
    /// icon is not invisible outside KDE. The host application is expected to
    /// override it, which is exactly what KIconLoader does — so Dolphin draws
    /// the scheme's highlight and, reading the file honestly, we drew Tela's
    /// stand-in blue.
    ///
    /// Injected AFTER the file's own rules so ours win, and only for names we
    /// genuinely have a colour for: an unmapped class keeps the icon's fallback,
    /// which is better than inventing one. Same principle as everywhere else
    /// here — consume the desktop's own data rather than reimplementing it.
    ///
    /// The drawn-icon cache is already invalidated on a scheme change, which is
    /// what stops the old colour staying baked in.
    /// </summary>
    private static void ApplyColourScheme(
        Dictionary<string, (string? Fill, string? Colour, string? Stroke)> rules)
    {
        foreach (var (selector, resource) in SchemeClasses)
        {
            if (Application.Current?.Resources[resource] is not ISolidColorBrush brush) continue;

            var colour = brush.Color.ToString();

            // Only the `color` property: these classes are referenced through
            // `currentColor`, and overwriting a literal `fill` would repaint
            // parts of an icon that never asked to follow the scheme.
            rules[selector] = rules.TryGetValue(selector, out var existing)
                ? (existing.Fill, colour, existing.Stroke)
                : (null, colour, null);
        }
    }

    /// <summary>
    /// The KDE class names we can honestly answer for, mapped to theme
    /// resources. Positive, negative and neutral text are deliberately absent —
    /// this palette has no such roles, and a guess would be worse than the
    /// icon's own fallback.
    /// </summary>
    private static readonly (string Selector, string Resource)[] SchemeClasses =
    [
        (".ColorScheme-Text", "ViewText"),
        (".ColorScheme-Background", "ViewBackground"),
        (".ColorScheme-Highlight", "AccentColour"),
        (".ColorScheme-Contrast", "ViewText"),
    ];

    /// <summary>
    /// A property's value for an element, in CSS precedence order: the
    /// presentation attribute is weakest, then stylesheet rules, then the
    /// inline style attribute.
    /// </summary>
    private static string? Declared(
        XElement element, string property,
        Dictionary<string, (string? Fill, string? Colour, string? Stroke)>? styles)
    {
        var value = (string?)element.Attribute(property);

        if (styles is { Count: > 0 })
        {
            static string? Pick(
                (string? Fill, string? Colour, string? Stroke) rule, string property) => property switch
            {
                "fill" => rule.Fill,
                "stroke" => rule.Stroke,
                "color" => rule.Colour,
                _ => null,
            };

            if (styles.TryGetValue(element.Name.LocalName, out var byTag)
                && Pick(byTag, property) is { } tagValue)
                value = tagValue;

            foreach (var name in ((string?)element.Attribute("class") ?? "")
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (styles.TryGetValue("." + name, out var byClass)
                    && Pick(byClass, property) is { } classValue)
                    value = classValue;
            }

            if ((string?)element.Attribute("id") is { Length: > 0 } id
                && styles.TryGetValue("#" + id, out var byId)
                && Pick(byId, property) is { } idValue)
                value = idValue;
        }

        if ((string?)element.Attribute("style") is { Length: > 0 } inline)
        {
            foreach (var declaration in inline.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = declaration.Split(':', 2);
                if (pair.Length == 2 && pair[0].Trim() == property) value = pair[1].Trim();
            }
        }

        return value;
    }

    // ---- gradients ------------------------------------------------------

    /// <summary>
    /// Builds every gradient in the document up front, keyed by id, so a
    /// fill="url(#x)" is a lookup. Two passes because gradients commonly carry
    /// only geometry and inherit their stops from another via href.
    /// </summary>
    private static Dictionary<string, IBrush> ReadGradients(XElement root, Rect bounds)
    {
        var elements = root.Descendants()
            .Where(e => e.Name.LocalName is "linearGradient" or "radialGradient")
            .Where(e => (string?)e.Attribute("id") is { Length: > 0 })
            .ToDictionary(e => (string)e.Attribute("id")!, e => e);

        var result = new Dictionary<string, IBrush>(StringComparer.Ordinal);

        foreach (var (id, element) in elements)
        {
            var stops = StopsFor(element, elements, depth: 0);
            if (stops.Count == 0) continue;

            // objectBoundingBox is the SVG default and maps directly onto
            // Avalonia's relative units. userSpaceOnUse is in viewBox
            // coordinates, so it is converted rather than declined.
            var absolute = (string?)element.Attribute("gradientUnits") == "userSpaceOnUse";

            // gradientTransform, applied to the AXIS in user space.
            //
            // Avalonia's brush has no equivalent property, and for an affine
            // transform it does not need one: a linear gradient is defined by
            // its two endpoints, so mapping those through the matrix maps the
            // gradient. Ignoring it is not a cosmetic loss — Tela's folder
            // shadow declares its axis at x=-197.72 and relies on
            // rotate(-45,-337.55,-145.8) to carry it onto the icon, so without
            // this the axis lands far outside the shape, every pixel takes the
            // offset-0 stop, and a soft corner shading renders as a solid black
            // wedge.
            var matrix = ReadTransform(element, "gradientTransform")?.Value;

            // Row-vector convention, matching the matrix(a b c d e f) mapping
            // a few lines down in ReadTransform.
            (double X, double Y) Map(double x, double y)
            {
                if (matrix is not { } m) return (x, y);

                return (x * m.M11 + y * m.M21 + m.M31,
                        x * m.M12 + y * m.M22 + m.M32);
            }

            // **userSpaceOnUse maps to Avalonia's ABSOLUTE unit, not to a
            // fraction of the viewBox.**
            //
            // This previously divided by the viewBox, which looks right and is
            // not: a Relative point is 0..1 of the BOUNDS OF THE SHAPE BEING
            // FILLED, not of the icon. For a folder body those two rectangles
            // are nearly the same and the error is invisible — but Tela's corner
            // shadow is a small triangle, so a 10-unit fade got squeezed into a
            // fraction of that triangle's width and rendered as a hard stripe.
            // That difference is exactly why file icons looked correct while
            // folders did not.
            var unit = absolute ? RelativeUnit.Absolute : RelativeUnit.Relative;

            if (element.Name.LocalName == "linearGradient")
            {
                var (sx, sy) = Map(
                    Number(element, "x1", absolute ? bounds.X : 0),
                    Number(element, "y1", absolute ? bounds.Y : 0));

                var (ex, ey) = Map(
                    Number(element, "x2", absolute ? bounds.Right : 1),
                    Number(element, "y2", absolute ? bounds.Y : 0));

                result[id] = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(sx, sy, unit),
                    EndPoint = new RelativePoint(ex, ey, unit),
                    GradientStops = stops,
                };
            }
            else
            {
                var (cx, cy) = Map(
                    Number(element, "cx", absolute ? bounds.Center.X : 0.5),
                    Number(element, "cy", absolute ? bounds.Center.Y : 0.5));

                var (fx, fy) = Map(
                    Number(element, "fx", absolute ? bounds.Center.X : 0.5),
                    Number(element, "fy", absolute ? bounds.Center.Y : 0.5));

                result[id] = new RadialGradientBrush
                {
                    Center = new RelativePoint(cx, cy, unit),
                    GradientOrigin = new RelativePoint(fx, fy, unit),
                    // RadiusX/RadiusY as RelativeScalar, not a single Radius
                    // double — an SVG radial gradient is circular, so both take
                    // the same value.
                    // Radius follows the same unit as the centre. A
                    // gradientTransform that scales would also scale this, which
                    // is not handled — no icon here has needed it, and guessing
                    // at an average scale factor would be worse than leaving it.
                    RadiusX = new RelativeScalar(
                        Number(element, "r", absolute ? bounds.Width / 2 : 0.5), unit),
                    RadiusY = new RelativeScalar(
                        Number(element, "r", absolute ? bounds.Height / 2 : 0.5), unit),
                    GradientStops = stops,
                };
            }
        }

        return result;
    }

    private static GradientStops StopsFor(
        XElement element, Dictionary<string, XElement> all, int depth)
    {
        var stops = new GradientStops();

        foreach (var stop in element.Elements().Where(e => e.Name.LocalName == "stop"))
        {
            var offset = Percentage((string?)stop.Attribute("offset"));
            var colour = StopColour(stop);
            stops.Add(new GradientStop(colour, offset));
        }

        if (stops.Count > 0 || depth > 4) return stops;

        // href/xlink:href — the stops live on another gradient.
        var reference = (string?)stop_href(element);
        if (reference is { Length: > 1 } && reference[0] == '#'
            && all.TryGetValue(reference[1..], out var parent))
            return StopsFor(parent, all, depth + 1);

        return stops;

        static XAttribute? stop_href(XElement e)
            => e.Attribute("href")
               ?? e.Attribute(XNamespace.Get("http://www.w3.org/1999/xlink") + "href");
    }

    /// <summary>
    /// How much of a black gradient stop's opacity to keep. See StopColour for
    /// why this exists and why it is limited to black.
    /// </summary>
    private const double ShadowSoftness = 0.55;

    private static Color StopColour(XElement stop)
    {
        var colour = (string?)stop.Attribute("stop-color");
        var opacity = Number(stop, "stop-opacity", 1.0);

        // Inline style wins, which is how most editors write stops out.
        if ((string?)stop.Attribute("style") is { Length: > 0 } style)
        {
            foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = declaration.Split(':', 2);
                if (pair.Length != 2) continue;

                if (pair[0].Trim() == "stop-color") colour = pair[1].Trim();
                else if (pair[0].Trim() == "stop-opacity"
                         && double.TryParse(pair[1].Trim(), NumberStyles.Float,
                             CultureInfo.InvariantCulture, out var parsed)) opacity = parsed;
            }
        }

        var parsedColour = Colors.Black;
        try { if (colour is { Length: > 0 }) parsedColour = Color.Parse(colour); }
        catch { /* leave black */ }

        // **A DELIBERATE DEVIATION FROM THE FILE**, and the only one in this
        // renderer — everything else here draws what the SVG says.
        //
        // [stated] "The shadow is just a little too hard. it could be softer
        // slightly." Icon sets author their corner shadows at full black for a
        // large canvas; at 17 px in a sidebar and 48 px in a tile the fade
        // crosses only a handful of pixels, so the dark end reads as an edge
        // rather than as shading.
        //
        // Applied ONLY to a black stop, which is what a shadow is. A coloured
        // gradient is the icon's own artwork and is left alone — dimming those
        // would wash out every logo in the theme.
        //
        // ShadowSoftness is the single dial: raise it toward 1.0 for the file's
        // own weight, lower it for less. Deleting the two lines restores exact
        // fidelity.
        if (parsedColour is { R: 0, G: 0, B: 0 }) opacity *= ShadowSoftness;

        return Color.FromArgb((byte)(parsedColour.A * Math.Clamp(opacity, 0, 1)),
            parsedColour.R, parsedColour.G, parsedColour.B);
    }

    private static double Percentage(string? raw)
    {
        if (raw is null) return 0;

        var text = raw.Trim();
        var percent = text.EndsWith('%');
        if (percent) text = text[..^1];

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(percent ? value / 100 : value, 0, 1)
            : 0;
    }

    // ---- shapes ---------------------------------------------------------

    private static Rect ReadViewBox(XElement root)
    {
        var raw = (string?)root.Attribute("viewBox");

        if (raw is not null)
        {
            var parts = raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 4
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                return new Rect(x, y, w, h);
        }

        return new Rect(0, 0, 16, 16);
    }

    /// <summary>
    /// translate/scale/matrix. Ignoring transforms meant any icon that placed
    /// its parts by transform drew them in the wrong place or not at all.
    /// </summary>
    private static Transform? ReadTransform(XElement element, string attribute = "transform")
    {
        var raw = (string?)element.Attribute(attribute);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var group = new TransformGroup();

        foreach (Match match in Regex.Matches(raw, @"(\w+)\s*\(([^)]*)\)"))
        {
            var numbers = match.Groups[2].Value
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var parsed) ? parsed : 0)
                .ToArray();

            switch (match.Groups[1].Value)
            {
                case "translate" when numbers.Length >= 1:
                    group.Children.Add(new TranslateTransform(
                        numbers[0], numbers.Length > 1 ? numbers[1] : 0));
                    break;

                case "scale" when numbers.Length >= 1:
                    group.Children.Add(new ScaleTransform(
                        numbers[0], numbers.Length > 1 ? numbers[1] : numbers[0]));
                    break;

                case "matrix" when numbers.Length >= 6:
                    group.Children.Add(new MatrixTransform(new Matrix(
                        numbers[0], numbers[1], numbers[2],
                        numbers[3], numbers[4], numbers[5])));
                    break;

                // SVG's rotate takes an OPTIONAL centre: rotate(angle cx cy).
                // Dropping it rotates about the origin instead, which for a
                // centre far from it — Tela's folder shadow uses
                // rotate(-45,-337.55,-145.8) — puts the result nowhere near the
                // icon. This was silently wrong for element transforms too, not
                // only gradients.
                case "rotate" when numbers.Length >= 3:
                    group.Children.Add(new RotateTransform(numbers[0])
                    {
                        CenterX = numbers[1],
                        CenterY = numbers[2],
                    });
                    break;

                case "rotate" when numbers.Length >= 1:
                    group.Children.Add(new RotateTransform(numbers[0]));
                    break;
            }
        }

        return group.Children.Count > 0 ? group : null;
    }

    private static void Walk(
        XElement element, DrawingGroup group, IBrush? inheritedFill,
        Dictionary<string, IBrush> gradients,
        Dictionary<string, (string? Fill, string? Colour, string? Stroke)> styles)
    {
        var root = element.AncestorsAndSelf().Last();

        foreach (var child in element.Elements())
        {
            // defs holds paint definitions, not drawable content.
            if (child.Name.LocalName is "defs" or "linearGradient" or "radialGradient") continue;

            var fill = ReadFill(child, gradients, styles) ?? inheritedFill;

            // A transform puts its subtree into its own group.
            var target = group;
            if (ReadTransform(child) is { } transform)
            {
                target = new DrawingGroup { Transform = transform };
                group.Children.Add(target);
            }

            switch (child.Name.LocalName)
            {
                case "g":
                    Walk(child, target, fill, gradients, styles);
                    break;

                // <use> draws another element by id. Tela builds its file icons
                // from a shared page shape plus a coloured badge, so skipping
                // this left only the badge — a tiny mark instead of an icon.
                case "use":
                    var reference = (string?)child.Attribute("href")
                        ?? (string?)child.Attribute(
                            XNamespace.Get("http://www.w3.org/1999/xlink") + "href");

                    if (reference is not { Length: > 1 } || reference[0] != '#') break;

                    var referenced = root.Descendants()
                        .FirstOrDefault(e => (string?)e.Attribute("id") == reference[1..]);

                    if (referenced is null) break;

                    var placed = new DrawingGroup
                    {
                        Transform = new TranslateTransform(
                            Number(child, "x"), Number(child, "y")),
                    };

                    target.Children.Add(placed);
                    DrawOne(referenced, placed, fill, gradients, styles);
                    break;

                case "path" when (string?)child.Attribute("d") is { Length: > 0 } data:
                    Add(target, Geometry.Parse(data), fill, child, styles);
                    break;

                case "rect":
                    Add(target, new RectangleGeometry(new Rect(
                        Number(child, "x"), Number(child, "y"),
                        Number(child, "width"), Number(child, "height"))), fill, child, styles);
                    break;

                case "circle":
                    Add(target, new EllipseGeometry(new Rect(
                        Number(child, "cx") - Number(child, "r"),
                        Number(child, "cy") - Number(child, "r"),
                        Number(child, "r") * 2, Number(child, "r") * 2)), fill, child, styles);
                    break;

                case "ellipse":
                    Add(target, new EllipseGeometry(new Rect(
                        Number(child, "cx") - Number(child, "rx"),
                        Number(child, "cy") - Number(child, "ry"),
                        Number(child, "rx") * 2, Number(child, "ry") * 2)), fill, child, styles);
                    break;

                case "polygon" when PolyGeometry(child, close: true) is { } polygon:
                    Add(target, polygon, fill, child, styles);
                    break;

                case "polyline" when PolyGeometry(child, close: false) is { } polyline:
                    Add(target, polyline, fill, child, styles);
                    break;
            }
        }
    }

    /// <summary>Draws one element, used by &lt;use&gt; to render its referent.</summary>
    private static void DrawOne(
        XElement element, DrawingGroup group, IBrush? fill,
        Dictionary<string, IBrush> gradients,
        Dictionary<string, (string? Fill, string? Colour, string? Stroke)> styles)
    {
        var own = ReadFill(element, gradients, styles) ?? fill;

        switch (element.Name.LocalName)
        {
            case "g":
                Walk(element, group, own, gradients, styles);
                break;

            case "path" when (string?)element.Attribute("d") is { Length: > 0 } data:
                Add(group, Geometry.Parse(data), own, element, styles);
                break;

            case "rect":
                Add(group, new RectangleGeometry(new Rect(
                    Number(element, "x"), Number(element, "y"),
                    Number(element, "width"), Number(element, "height"))), own, element, styles);
                break;

            case "circle":
                Add(group, new EllipseGeometry(new Rect(
                    Number(element, "cx") - Number(element, "r"),
                    Number(element, "cy") - Number(element, "r"),
                    Number(element, "r") * 2, Number(element, "r") * 2)), own, element, styles);
                break;
        }
    }

    private static void Add(
        DrawingGroup group, Geometry geometry, IBrush? fill, XElement source,
        Dictionary<string, (string? Fill, string? Colour, string? Stroke)>? styles = null)
    {
        var opacity = Number(source, "opacity", 1.0);
        if (opacity <= 0.01) return;

        // fill="none" is explicit and must not fall back to the text colour —
        // an outline-only shape has no fill by design.
        var declared = Declared(source, "fill", styles);
        var filled = declared != "none";

        IBrush? brush = null;
        if (filled)
        {
            brush = Fade(fill ?? CurrentColour(),
                Number(source, "fill-opacity", 1.0) * opacity);
        }

        // Strokes were ignored entirely, so any icon drawn as outlines rendered
        // almost nothing — which is indistinguishable from a tiny icon.
        Pen? pen = null;
        var stroke = Declared(source, "stroke", styles);

        if (stroke is { Length: > 0 } && stroke != "none")
        {
            var width = Number(source, "stroke-width", 1.0);
            var colour = stroke.Equals("currentColor", StringComparison.OrdinalIgnoreCase)
                ? CurrentColour()
                : SafeBrush(stroke);

            if (colour is not null && width > 0)
                pen = new Pen(Fade(colour, Number(source, "stroke-opacity", 1.0) * opacity), width);
        }

        if (brush is null && pen is null) return;

        group.Children.Add(new GeometryDrawing { Geometry = geometry, Brush = brush, Pen = pen });
    }

    private static IBrush Fade(IBrush brush, double opacity)
    {
        if (opacity >= 0.99 || brush is not ISolidColorBrush solid) return brush;

        return new SolidColorBrush(Color.FromArgb(
            (byte)(solid.Color.A * Math.Clamp(opacity, 0, 1)),
            solid.Color.R, solid.Color.G, solid.Color.B));
    }


    private static IBrush? SafeBrush(string value)
    {
        try { return new SolidColorBrush(Color.Parse(value)); }
        catch { return null; }
    }


    /// <summary>points="x,y x,y ..." — a polygon closes, a polyline does not.</summary>
    private static Geometry? PolyGeometry(XElement element, bool close)
    {
        var raw = (string?)element.Attribute("points");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var numbers = raw.Split([' ', ',', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var parsed) ? parsed : 0)
            .ToArray();

        if (numbers.Length < 4) return null;

        var builder = new System.Text.StringBuilder();
        for (var i = 0; i + 1 < numbers.Length; i += 2)
        {
            builder.Append(i == 0 ? 'M' : 'L')
                   .Append(numbers[i].ToString(CultureInfo.InvariantCulture))
                   .Append(',')
                   .Append(numbers[i + 1].ToString(CultureInfo.InvariantCulture))
                   .Append(' ');
        }

        if (close) builder.Append('Z');

        try { return Geometry.Parse(builder.ToString()); }
        catch { return null; }
    }

    private static IBrush? ReadFill(
        XElement element, Dictionary<string, IBrush> gradients,
        Dictionary<string, (string? Fill, string? Colour, string? Stroke)>? styles = null)
    {
        var value = Declared(element, "fill", styles);

        if (string.IsNullOrWhiteSpace(value) || value == "none") return null;

        // url(#id) — a gradient defined elsewhere in the document.
        if (value.StartsWith("url(", StringComparison.Ordinal))
        {
            var id = value.Trim()[4..].TrimEnd(')').Trim().TrimStart('#').Trim('"', '\'');
            return gradients.TryGetValue(id, out var brush) ? brush : null;
        }

        // currentColor means "whatever the surrounding text is", resolved from
        // the live theme so symbolic icons follow the colour scheme.
        if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            // A stylesheet may define the colour that "currentColor" refers to;
            // symbolic icon sets are built exactly that way.
            if (Declared(element, "color", styles) is { Length: > 0 } declared
                && !declared.Equals("currentColor", StringComparison.OrdinalIgnoreCase)
                && SafeBrush(declared) is { } themed)
                return themed;

            return CurrentColour();
        }

        try { return new SolidColorBrush(Color.Parse(value)); }
        catch { return null; }
    }

    private static readonly Dictionary<string, IImage> Fallbacks = new(StringComparer.Ordinal);

    /// <summary>
    /// The drawn glyph, as an image.
    ///
    /// It used to be Path elements sitting behind the themed icon in the same
    /// Panel, which meant both drew whenever a theme supplied one — three
    /// stacked layers relying on each other to mask, which they did not. One
    /// element with one source cannot overlap itself.
    /// **UI thread only**, like Load.
    /// </summary>
    public static IImage Fallback(bool isDirectory)
    {
        var accent = (Application.Current?.Resources["AccentColour"] as ISolidColorBrush)?.Color
                     ?? Colors.SteelBlue;
        var dim = (Application.Current?.Resources["ViewDimText"] as ISolidColorBrush)?.Color
                  ?? Colors.Gray;

        // Keyed by colour so a theme change produces a new drawing rather than
        // serving a stale one.
        var key = $"{isDirectory}|{accent}|{dim}";
        if (Fallbacks.TryGetValue(key, out var cached)) return cached;

        var group = new DrawingGroup();

        // The 16-unit box the two-tone glyph used is kept for the file, and the
        // folder now draws on the design's own 64 x 54 canvas — so the clip
        // below has to follow whichever is in use rather than being a constant.
        var canvas = isDirectory ? new Rect(0, 0, 64, 54) : new Rect(0, 0, 16, 16);

        if (isDirectory)
        {
            // **The design reference's folder, copied.** Body, then the flap's
            // lit edge, then the seam under it — three fixed colours from
            // `Vaktari Window.dc.html`, which is why this no longer takes the
            // accent. That is the same trade as the palette override in
            // ThemeApplier: 1:1 with the mock costs the desktop its say.
            // Revert by restoring the accent brushes on the old 16-unit paths.
            group.Children.Add(new GeometryDrawing
            {
                Geometry = Geometry.Parse("M3 11 h20 l4 5 h34 v34 H3 Z"),
                Brush = new SolidColorBrush(Color.Parse("#5457dd")),
            });
            group.Children.Add(new GeometryDrawing
            {
                Geometry = Geometry.Parse("M3 11 h20 l4 5"),
                Pen = new Pen(new SolidColorBrush(Color.Parse("#cfd0ff")), 2),
            });
            group.Children.Add(new GeometryDrawing
            {
                Geometry = Geometry.Parse("M3 16 H61"),
                Pen = new Pen(new SolidColorBrush(Color.Parse("#8f91ff")), 1.5),
            });
        }
        else
        {
            group.Children.Add(new GeometryDrawing
            {
                Geometry = Geometry.Parse("M3,1.5 L10,1.5 L13,4.5 L13,14.5 L3,14.5 Z"),
                Brush = new SolidColorBrush(dim, 0.75),
            });
        }

        var image = new DrawingImage
        {
            Drawing = new DrawingGroup
            {
                ClipGeometry = new RectangleGeometry(canvas),
                Children = { group },
            },
        };

        Fallbacks[key] = image;
        return image;
    }

    /// <summary>The live text colour, so symbolic icons follow the scheme.</summary>
    private static IBrush CurrentColour()
        => Application.Current?.Resources["ViewText"] as IBrush ?? Brushes.Gray;

    private static double Number(XElement element, string name, double fallback = 0)
        => double.TryParse((string?)element.Attribute(name),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
