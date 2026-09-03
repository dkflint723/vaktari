using Avalonia.Media;

namespace Vaktari.Ui.Thumbnails;

/// <summary>
/// The emblem drawn over a shortcut, a symlink or a junction.
///
/// **A link was drawn exactly like the thing it points at.** A link to a folder
/// was a folder and a link to a program was that program, in all three
/// listings — and the flag saying so has been on every entry and correct all
/// along, with nothing anywhere reading it.
///
/// Two shapes rather than one, because they are not the same kind of mark: the
/// ground is filled and the arrow is stroked, and a single Path has one Fill.
///
/// **The ground is the whole point, and it is the one place this set breaks its
/// own rule.** Everything else drawn here is stroke on nothing — but those sit
/// on the sidebar's own background, which they can rely on. This lands on
/// whatever the shell or the icon theme drew, which on Windows is a full-colour
/// bitmap: a bare arrow over a dark thumbnail is not an emblem, it is a rumour.
/// [stated] the choice between a bare arrow, this, and a right-angled elbow was
/// made by looking at all three at 16px.
///
/// Held here rather than written into the three row templates because they are
/// kept in step by hand, and the alignment defect in the details heading was
/// exactly what that costs. One definition, three references.
///
/// Drawn on the same 24×24 grid as <see cref="SidebarIcon"/>, in the
/// bottom-left corner — the corner Explorer uses, and the one a name never
/// grows into.
/// </summary>
public static class LinkEmblem
{
    /// <summary>The filled corner the arrow sits on.</summary>
    public static Geometry Ground { get; } =
        Geometry.Parse("M2.6 12.2 H10.6 a2 2 0 0 1 2 2 V21.4 H2.6 Z");

    /// <summary>The diagonal and its head.</summary>
    public static Geometry Arrow { get; } =
        Geometry.Parse("M3.6 20.4 L9.6 14.4 M9.6 14.4 H6.0 M9.6 14.4 V18.0");
}
