using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// An eject dismounts a volume only once it holds the lock.
///
/// **A refused lock was followed by a dismount anyway** — a forced one, which
/// invalidates every handle open on the volume, so a program saving to the
/// stick was cut off mid-write before the removal was even asked. The drive
/// itself cannot be staged here; the rule can.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EjectLockTests
{
    [WindowsFact]
    public void With_the_lock_refused_nothing_is_dismounted()
    {
        var locks = 0;
        var dismounts = 0;

        var done = WindowsEjector.LockThenDismount(
            () => { locks++; return false; },
            () => { dismounts++; return true; },
            () => { },
            CancellationToken.None);

        Assert.False(done);
        Assert.True(locks > 1, "the lock was not retried");
        Assert.Equal(0, dismounts);
    }

    /// <summary>
    /// **A disc in use keeps its tray shut.** The drive opens the tray
    /// whatever is open on the disc; a lock refused for an open file —
    /// access denied, or a sharing violation — is what stops it. A tray with
    /// nothing mounted cannot be locked either, and still opens.
    /// </summary>
    [WindowsFact]
    public void Only_a_lock_refused_for_an_open_file_keeps_the_tray_shut()
    {
        Assert.True(WindowsEjector.HeldOpen(5));
        Assert.True(WindowsEjector.HeldOpen(32));
        Assert.False(WindowsEjector.HeldOpen(21));
        Assert.False(WindowsEjector.HeldOpen(0));
    }

    [WindowsFact]
    public void With_the_lock_held_the_volume_is_dismounted()
    {
        var dismounts = 0;

        var done = WindowsEjector.LockThenDismount(
            () => true,
            () => { dismounts++; return true; },
            () => { },
            CancellationToken.None);

        Assert.True(done);
        Assert.Equal(1, dismounts);
    }
}
