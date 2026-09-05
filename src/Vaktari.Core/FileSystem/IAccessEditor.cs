namespace Vaktari.Core.FileSystem;

/// <summary>
/// One editable access flag. Grouped and labelled by the platform, because
/// POSIX mode bits and Windows attributes share nothing but the idea of
/// "what may be done with this file" — a typed model would have to be a union
/// of both and would fit neither.
/// </summary>
public sealed record AccessToggle(string Key, string Group, string Label, bool Value);

/// <summary>
/// Who a file belongs to, and who it could belong to.
///
/// **Owner and group reached the window as two lines of text.** They are two
/// thirds of what a POSIX mode MEANS -- "group: read, write" says nothing until
/// you know which group -- and a dialog that lets you set the bits and not the
/// principals answers half the question. Dolphin and Nautilus both offer them.
///
/// <paramref name="Owners"/> and <paramref name="Groups"/> are what a chooser
/// may offer, which is not the same as everything that exists: a person who is
/// not root may only hand a file to a group they are in, so the list is theirs
/// rather than the machine's.
/// </summary>
public sealed record Ownership(
    string Owner,
    string Group,
    IReadOnlyList<string> Owners,
    IReadOnlyList<string> Groups,
    bool CanChangeOwner,
    bool CanChangeGroup);

public sealed record AccessState(IReadOnlyList<AccessToggle> Toggles, string Summary)
{
    /// <summary>
    /// Null where the platform has no such notion, which is how the window
    /// decides whether to draw the two choosers at all.
    ///
    /// An init-only member rather than a fourth positional one: every existing
    /// construction of this record is a platform's own, and adding a required
    /// parameter would have made "no ownership" impossible to say.
    /// </summary>
    public Ownership? Ownership { get; init; }
}

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

    /// <summary>
    /// Hands the file to somebody else.
    ///
    /// Both at once, because the tool underneath takes both at once and two
    /// calls would leave a file half moved when the second was refused.
    /// </summary>
    /// <returns>
    /// Null when it took, and the reason in words when it did not.
    ///
    /// **Words rather than a bool**, for the reason the recursive apply above
    /// already gives: "changing the owner needs root" and "there is no such
    /// group" are different problems with different answers, and a dialog that
    /// says only "failed" sends somebody to a terminal to find out which.
    ///
    /// Defaulted to a refusal so a platform with no notion of ownership does
    /// not have to write one -- the window never asks, because
    /// <see cref="AccessState.Ownership"/> is null there.
    /// </returns>
    ValueTask<string?> SetOwnershipAsync(
        string path, string owner, string group, bool recursive, CancellationToken ct)
        => ValueTask.FromResult<string?>("this platform does not have file owners");
}

/// <summary>
/// What a recursive apply could not do. <see cref="Skipped"/> zero means every
/// entry took the change.
/// </summary>
public readonly record struct AccessOutcome(int Skipped, Exception? FirstFailure)
{
    public static readonly AccessOutcome Complete = new(0, null);
}
