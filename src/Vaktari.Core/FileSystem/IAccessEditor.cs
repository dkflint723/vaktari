namespace Vaktari.Core.FileSystem;

/// <summary>
/// One editable access flag. Grouped and labelled by the platform, because
/// POSIX mode bits and Windows attributes share nothing but the idea of
/// "what may be done with this file" — a typed model would have to be a union
/// of both and would fit neither.
/// </summary>
public sealed record AccessToggle(string Key, string Group, string Label, bool Value);

public sealed record AccessState(IReadOnlyList<AccessToggle> Toggles, string Summary);

public interface IAccessEditor
{
    /// <summary>False where the platform offers nothing editable; the UI then
    /// hides the section rather than showing something that cannot be applied.</summary>
    bool CanEdit { get; }

    ValueTask<AccessState?> GetAccessAsync(string path, CancellationToken ct);

    /// <summary>
    /// Applies the toggles. When <paramref name="recursive"/>, directories get
    /// the execute bit wherever the matching read bit is set — the "X" rule
    /// from chmod. Without it, a recursive 644 makes every directory in the
    /// tree untraversable, which is a genuinely destructive footgun.
    /// </summary>
    /// <returns>
    /// How many entries could NOT be changed, and the first reason why.
    ///
    /// **Returned rather than swallowed.** A recursive apply skips whatever it
    /// cannot write and used to report "applied" regardless, so a tree where
    /// every child belonged to another user looked exactly like one where the
    /// change took — which is the worst possible answer for a permissions
    /// dialog to give.
    /// </returns>
    ValueTask<AccessOutcome> SetAccessAsync(
        string path,
        IReadOnlyList<AccessToggle> toggles,
        bool recursive,
        IProgress<int>? progress,
        CancellationToken ct);
}

/// <summary>
/// What a recursive apply could not do. <see cref="Skipped"/> zero means every
/// entry took the change.
/// </summary>
public readonly record struct AccessOutcome(int Skipped, Exception? FirstFailure)
{
    public static readonly AccessOutcome Complete = new(0, null);
}
