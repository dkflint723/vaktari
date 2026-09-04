using System.Collections.Concurrent;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui;

/// <summary>
/// Trimming for a filename, where the ellipsis goes in the MIDDLE so that the
/// extension survives.
///
/// **A name that did not fit was cut at the end, and the extension is the end.**
/// Every listing row asked for <c>CharacterEllipsis</c>, which fills from the
/// left and stops: measured in the shipped details row, a 160px name cell drew
/// "quarterly-forecast-final-revision.xlsx" as "quarterly-…" — a name that no
/// longer says whether it is a spreadsheet, a photograph or an installer.
/// The one column that would have said so is Type, which is off by default
/// (<c>PaneViewModel.ShowTypeColumn</c> has no initialiser) and exists in one
/// layout of the three, so for most people the extension was the only thing in
/// the window carrying that fact and it was the first thing thrown away.
///
/// The same reasoning the delete confirmation already runs on — see
/// <c>Confirmations.Elide</c>, which elides a name in the middle so ".pdf"
/// against ".exe" survives — except that a dialog can count characters and a
/// column cannot: a proportional font makes "WWW" three times the width of
/// "iii", so where to cut is a question about the drawn width and only the text
/// layout knows the answer.
///
/// Applied through <see cref="TextTrimming"/> rather than by splitting the cell
/// into a stem control and an extension control, because a docked pair only
/// works in a single-line left-aligned cell — the tiles centre and wrap — and
/// because the name cell is one TextBlock in all three templates, which is what
/// the row-name and name-tooltip tests read.
/// </summary>
public sealed class NameEllipsis : TextTrimming
{
    /// <summary>
    /// The one instance. A trimming carries no per-use state — everything that
    /// varies arrives in <see cref="TextCollapsingCreateInfo"/> — and a row
    /// template that allocated one per row would allocate one per row.
    /// </summary>
    public static readonly NameEllipsis KeepingTheExtension = new();

    private NameEllipsis() { }

    public override TextCollapsingProperties CreateCollapsingProperties(TextCollapsingCreateInfo createInfo)
        => new KeepTheExtension(createInfo);

    /// <summary>
    /// Where the cut is decided. One of these is built per line that overflows,
    /// which is per trimmed row rather than per row.
    /// </summary>
    private sealed class KeepTheExtension : TextCollapsingProperties
    {
        private const string Ellipsis = "…";

        /// <summary>
        /// The drawn width of "…" per typeface and size.
        ///
        /// The head can only be measured against the room left over once the
        /// ellipsis and the extension are paid for, and the ellipsis is not in
        /// the line being collapsed, so it has to be laid out on its own. A
        /// listing uses two font sizes and one family, so this settles at a
        /// couple of entries and never grows with the number of rows.
        /// </summary>
        private static readonly ConcurrentDictionary<(Typeface Face, double Size), double> EllipsisWidths = new();

        private readonly TextRunProperties _properties;

        internal KeepTheExtension(TextCollapsingCreateInfo createInfo)
        {
            Width = createInfo.Width;
            FlowDirection = createInfo.FlowDirection;
            _properties = createInfo.TextRunProperties;
        }

        public override double Width { get; }

        /// <summary>
        /// Part of the base class's contract. Built on demand rather than in
        /// the constructor: the collapses below shape their own ellipsis, so
        /// this would otherwise be an allocation per trimmed row that nothing
        /// reads.
        /// </summary>
        public override TextRun Symbol => new TextCharacters(Ellipsis, _properties);

        public override FlowDirection FlowDirection { get; }

        public override TextRun[]? Collapse(TextLine textLine)
        {
            var text = LineText(textLine);

            // The listing's own rule, not a second copy of it: a leading dot
            // begins a name rather than an extension, so ".gitignore" has none
            // and is trimmed from the end like any other word.
            var extension = FileEntry.ExtensionOf(text);

            if (extension.IsEmpty) return Trailing().Collapse(textLine);

            // Character hits are indices into the TEXT SOURCE, not into the
            // line: a wrapped line's second row starts partway through the
            // string and its own first character is at FirstTextSourceIndex.
            var start = textLine.FirstTextSourceIndex;
            var dot = text.Length - extension.Length - 1;

            var whole = textLine.GetDistanceFromCharacterHit(new CharacterHit(start + text.Length));
            var beforeExtension = textLine.GetDistanceFromCharacterHit(new CharacterHit(start + dot));

            var room = Width - EllipsisWidth(_properties) - (whole - beforeExtension);

            // The last character that still starts inside the room left over.
            // `room` goes negative in a cell narrower than the ellipsis and the
            // extension together, and a negative distance answers the line's
            // first character — measured — so that case arrives at the guard
            // below rather than needing one of its own.
            var head = textLine.GetCharacterHitFromDistance(room).FirstCharacterIndex - start;

            // A cell too narrow to hold one character, the ellipsis and the
            // extension has nothing to gain from the middle: an ellipsis with
            // nothing in front of it says less than the first letters do, and a
            // listing is scanned down its left edge.
            if (head < 1) return Trailing().Collapse(textLine);

            // The framework's own leading-prefix collapse does the shaping.
            // It keeps `head` characters, draws the ellipsis, and then fills
            // what is left from the END of the line — so the extension is what
            // survives, and a wide cell gets more of the name back with it.
            return new TextLeadingPrefixCharacterEllipsis(
                       Ellipsis, head, Width, _properties, FlowDirection)
                   .Collapse(textLine);
        }

        /// <summary>What CharacterEllipsis does, for the names with no extension
        /// to save and the cells with no room to save it in.</summary>
        private TextTrailingCharacterEllipsis Trailing()
            => new(Ellipsis, Width, _properties, FlowDirection);

        /// <summary>
        /// The characters this line draws.
        ///
        /// Built rather than read off the line, because the line's own
        /// <c>Length</c> is not the number of characters in it. Measured: a
        /// TextBlock bound to one string hands this two runs — the shaped
        /// characters, and a TextEndOfParagraph whose Length is 1 and whose
        /// Text is empty — so the line calls itself one longer than what it
        /// draws, and the arithmetic below is in drawn characters.
        ///
        /// One string per line that overflows, which is per TRIMMED row rather
        /// than per row, and only while one is being laid out.
        /// </summary>
        private static string LineText(TextLine textLine)
        {
            var text = new StringBuilder();

            foreach (var run in textLine.TextRuns) text.Append(run.Text.Span);

            return text.ToString();
        }

        private static double EllipsisWidth(TextRunProperties properties)
            => EllipsisWidths.GetOrAdd(
                (properties.Typeface, properties.FontRenderingEmSize),
                static key => new TextLayout(Ellipsis, key.Face, key.Size, null).Width);
    }
}
