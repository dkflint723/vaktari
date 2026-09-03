namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The sidebar's own sections, by the name they are folded under.
///
/// A section is remembered as folded by a KEY, and the keys come from two very
/// different places: a provider group is folded under its own label — "Places",
/// "Devices", whatever the desktop calls it — while these four are written into
/// the markup by hand and have no label to borrow.
///
/// **Prefixed, because the two share one namespace.** Folding is one set of
/// strings, matched without case, so a bare "network" would also fold the
/// provider group a places provider is perfectly entitled to call NETWORK — and
/// one of the test fakes already does. Only <see cref="Core.Places.PlaceGroups.Shares"/>
/// is guarded against that collision today, and it is guarded for a different
/// reason; the prefix removes the whole class rather than the one case someone
/// happened to think of.
/// </summary>
public static class SidebarSections
{
    public const string Network = "section:network";
    public const string Remote = "section:remote";
    public const string Sharing = "section:sharing";
    public const string Recent = "section:recent";
}
