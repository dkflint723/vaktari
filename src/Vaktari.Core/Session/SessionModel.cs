using Vaktari.Core.FileSystem;
using System.Text.Json.Serialization;

namespace Vaktari.Core.Session;

public enum SortField { Name, Size, Modified, Kind }

/// <summary>
/// How the listing itself is laid out. The Miller chain is deliberately NOT a
/// member: it is a navigation strip that sits above a layout, so it can be on
/// or off for either of these rather than being a third mutually exclusive mode.
/// </summary>
/// <summary>
/// Appending only: these persist as numbers, so reordering would silently
/// reinterpret every saved session.
/// </summary>
public enum ViewMode { Details, Grid, Compact }

/// <summary>Ctrl+B cycles full → rail only → hidden.</summary>
public enum RailState { Full, RailOnly, Hidden }

/// <summary>
/// State for one tab. Deliberately only fields that are actually read and
/// written — a schema that claims to store scroll position and doesn't is worse
/// than one that never promised. Scroll offset, selection, view mode and column
/// widths come back here when the features that own them exist.
/// </summary>
public sealed record TabState
{
    public required string Path { get; init; }
    public SortField Sort { get; init; } = SortField.Name;
    public bool SortDescending { get; init; }
    public bool ShowHidden { get; init; }
    public ViewMode View { get; init; } = ViewMode.Details;

    // ShowColumnStrip was here. The column browser it belonged to is gone, and
    // the field goes with it rather than being kept "in case" — a session file
    // written by an older build still carries the property, and
    // System.Text.Json ignores what it cannot bind, so old sessions load
    // cleanly without it. Nothing to migrate.

    /// <summary>
    /// Type and icon scale, per tab. A reference listing beside a working one
    /// wants different sizes, so these belong to the tab rather than the
    /// window. Zero or absent means "never set" and restores as 1.0.
    /// </summary>
    public double FontScale { get; init; } = 1.0;
    public double IconScale { get; init; } = 1.0;

    /// <summary>
    /// The other two layouts' scales. Details keeps the pair above, so an older
    /// session restores its details size and the rest start from it.
    ///
    /// **Zero means "never set", and that is deliberate rather than lazy.**
    /// Deserialization here does not run property initializers — an absent key
    /// arrives as `default(T)`, so the `= 1.0` on the fields above is decorative
    /// for any file written before they existed. Every reader must therefore
    /// treat 0 as absent, which is what `> 0 ? x : fallback` is doing at the
    /// restore site.
    /// </summary>
    public double GridFontScale { get; init; }
    public double GridIconScale { get; init; }
    public double CompactFontScale { get; init; }
    public double CompactIconScale { get; init; }

    /// <summary>Grouping is a view setting, so it belongs to the tab.</summary>
    public GroupMode GroupBy { get; init; } = GroupMode.None;

    /// <summary>
    /// Which details columns this tab shows. Per tab for the same reason sort
    /// and grouping are: a reference listing beside a working one wants
    /// different columns, and a choice made on one side of a split should not
    /// move the other.
    ///
    /// **Phrased so that false is what the tab did before these existed.**
    /// An absent key deserialises as default(T) — see the scales above — so a
    /// session written by an older build reads as size and modified shown,
    /// type not, which is exactly what it was showing.
    /// </summary>
    public bool HideSize { get; init; }
    public bool HideModified { get; init; }
    public bool ShowType { get; init; }

    /// <summary>
    /// Back/forward stacks, oldest first. Nobody restores navigation history —
    /// which is exactly why having it is noticeable.
    /// </summary>
    public IReadOnlyList<string> BackStack { get; init; } = [];
    public IReadOnlyList<string> ForwardStack { get; init; } = [];
}

public sealed record PaneState
{
    public IReadOnlyList<TabState> Tabs { get; init; } = [];
    public int ActiveTabIndex { get; init; }

    /// <summary>The details panel belongs to the split side, not the window.</summary>
    public bool IsInfoVisible { get; init; }
    public double InfoWidth { get; init; } = 280;
}

/// <summary>
/// Named WindowSession rather than WindowState because Avalonia's Window has a
/// WindowState property, and having both in scope in the code-behind is a trap.
/// </summary>
public sealed record WindowSession
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 1000;
    public double Height { get; init; } = 680;
    public bool IsMaximized { get; init; }

    /// <summary>One entry when unsplit, two when split.</summary>
    public IReadOnlyList<PaneState> Panes { get; init; } = [];
    public int ActivePaneIndex { get; init; }
    public double SplitRatio { get; init; } = 0.5;

    /// <summary>The details panel: a window setting, like the split.</summary>
    public bool IsInfoVisible { get; init; }
    public double InfoWidth { get; init; } = 280;

    /// <summary>Multiplies the whole type scale. Persisted because it is an
    /// accessibility setting, not a transient view state.</summary>
    /// <summary>
    /// Text and icons scale independently. One combined control could not
    /// express "large icons, small labels" or the reverse, and those are the
    /// two settings people actually reach for.
    /// </summary>
    public double FontScale { get; init; } = 1.0;
    public double IconScale { get; init; } = 1.0;

    /// <summary>
    /// The other two layouts' scales. Details keeps the pair above, so an older
    /// session restores its details size and the rest start from it.
    ///
    /// **Zero means "never set", and that is deliberate rather than lazy.**
    /// Deserialization here does not run property initializers — an absent key
    /// arrives as `default(T)`, so the `= 1.0` on the fields above is decorative
    /// for any file written before they existed. Every reader must therefore
    /// treat 0 as absent, which is what `> 0 ? x : fallback` is doing at the
    /// restore site.
    /// </summary>
    public double GridFontScale { get; init; }
    public double GridIconScale { get; init; }
    public double CompactFontScale { get; init; }
    public double CompactIconScale { get; init; }

    /// <summary>Grouping is a view setting, so it belongs to the tab.</summary>
    public GroupMode GroupBy { get; init; } = GroupMode.None;

    /// <summary>
    /// The right side as it was when the split was last closed. Reopening the
    /// split restores this rather than starting over, and it survives a restart
    /// so closing the split isn't a quiet way to lose a location.
    /// </summary>
    public PaneState? RememberedRightPane { get; init; }

    // ActiveSidebarPanel was here, and this is the SECOND time it has been
    // removed for the same reason. Its own comment recorded the first: "removed
    // in v2 precisely because nothing read or wrote them", then re-added in v3
    // "now that the sidebar exists" — at which point still nothing read it.
    // A QA sweep on 30 July 2026 found it persisted and restored with no way for
    // anyone to change it. If a third attempt is ever made, give it a consumer in
    // the same commit.
    public double SidebarWidth { get; init; } = 210;
    public RailState Rail { get; init; } = RailState.Full;
}

public sealed record SessionState
{
    /// <summary>
    /// v2 removed scroll/selection/view/column fields and added ShowHidden.
    /// v3 added the sidebar fields back, once there was a sidebar to store.
    /// v4 added SplitRatio, once split view existed.
    /// v5 added RememberedRightPane, so closing a split does not forget it.
    /// v6 added the per-tab View, once Miller columns existed.
    /// v7 added UiScale.
    /// v8 split UiScale into FontScale and IconScale, made ViewMode mean the
    /// listing layout (Details or Grid), and moved the Miller chain to its own
    /// ShowColumnStrip flag.
    /// An unrecognised version is ignored rather than migrated or thrown on —
    /// a session file must never prevent startup.
    /// </summary>
    public const int CurrentVersion = 13;   // v13: per-layout scales

    public int Version { get; init; } = CurrentVersion;
    public IReadOnlyList<WindowSession> Windows { get; init; } = [];
    public DateTimeOffset SavedAt { get; init; }
}

/// <summary>
/// Persistence contract. Implementations must:
///   1. debounce writes ~1s after any change, never save only on exit —
///      a crash or a reboot must not cost the session;
///   2. write atomically (tmp, flush, rename) and keep a .bak, because a
///      truncated file is what "it randomly forgets" actually looks like;
///   3. return null on any load failure so startup proceeds empty.
/// </summary>
public interface ISessionStore
{
    SessionState? Load();
    void NotifyChanged(SessionState state);
    ValueTask FlushAsync(CancellationToken ct);
}

/// <summary>Source-generated — reflection-based JSON does not survive trimming.</summary>
[JsonSerializable(typeof(SessionState))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class SessionJsonContext : JsonSerializerContext;
