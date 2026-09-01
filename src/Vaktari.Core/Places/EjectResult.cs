namespace Vaktari.Core.Places;

/// <summary>
/// How an eject ended.
///
/// **<see cref="Dismounted"/> is the reason this is an enum and not a bool**,
/// and it is the whole safety argument of the feature. There is a real state
/// where the filesystem has been flushed and torn down — the data is on the
/// device, nothing is outstanding — but the operating system has not released
/// the hardware, because something still holds it open at a level below the
/// filesystem. Both of the obvious two-state readings are lies about that:
///
/// - calling it success invites someone to pull a device the OS may still be
///   writing to, which is the one outcome here that destroys data;
/// - calling it failure teaches people the button does not work, so they pull
///   the device anyway, with no idea whether it was flushed.
///
/// Naming it is the only honest option: the data is safe, the row stays, and
/// the sentence says both.
/// </summary>
public enum EjectOutcome
{
    /// <summary>Flushed, torn down, and the system has let the device go.</summary>
    Ejected,

    /// <summary>Flushed and torn down, but the system still has the device.</summary>
    Dismounted,

    /// <summary>Something still has a file open. Nothing was torn down.</summary>
    InUse,

    /// <summary>Not something this machine can eject — a fixed disk, or a path
    /// that names no volume at all.</summary>
    NotRemovable,

    /// <summary>The tool this platform needs is not installed.</summary>
    NoTool,

    /// <summary>It did not work, and the message says what the system said.</summary>
    Failed,
}

/// <summary>
/// The outcome and the sentence to show for it.
///
/// A record rather than a bool because "something has a file open" and "udisks2
/// is not installed" lead the person to different actions — close a program
/// versus install a package — and a caller that only knows "it failed" cannot
/// tell them which. <c>IRemoteMounts.UnmountAsync</c> set the precedent of
/// returning a refusal rather than throwing one; this carries the reason too.
/// </summary>
public sealed record EjectResult(EjectOutcome Outcome, string Message)
{
    public static EjectResult Ejected(string message) => new(EjectOutcome.Ejected, message);
    public static EjectResult Dismounted(string message) => new(EjectOutcome.Dismounted, message);
    public static EjectResult InUse(string message) => new(EjectOutcome.InUse, message);
    public static EjectResult NotRemovable(string message) => new(EjectOutcome.NotRemovable, message);
    public static EjectResult NoTool(string message) => new(EjectOutcome.NoTool, message);
    public static EjectResult Failed(string message) => new(EjectOutcome.Failed, message);

    /// <summary>Whether the volume is expected to be gone from the next
    /// listing. Only a real ejection removes the row — a dismount leaves the
    /// device enumerated, and a row that vanished would say it is safe to
    /// unplug when it is not.</summary>
    public bool VolumeIsGone => Outcome is EjectOutcome.Ejected;
}

/// <summary>
/// Ejecting one volume, per platform.
///
/// Takes a path and nothing else — deliberately no "is it optical" flag. Both
/// implementations have to identify the device anyway to act on it, and learn
/// opticality from that step; a caller-supplied hint would be a second source
/// of truth that can disagree with the first.
/// </summary>
public interface IEjector
{
    Task<EjectResult> EjectAsync(string path, CancellationToken ct);
}
