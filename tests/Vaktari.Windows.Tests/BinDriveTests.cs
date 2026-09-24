using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Which drives the bin's icon asks about.
///
/// **Every mapped network drive was asked whether it was ready**, on the
/// window's thread, at startup and after every copy or delete — and a drive
/// whose server has gone does not answer. A network drive has no bin here, so
/// it is never asked.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BinDriveTests
{
    [WindowsFact]
    public void A_network_drive_is_never_asked_whether_it_is_ready()
    {
        var asked = 0;

        Assert.False(RecycleBin.Eligible(DriveType.Network, () => { asked++; return true; }));
        Assert.Equal(0, asked);
    }

    [WindowsFact]
    public void A_fixed_drive_is_asked_and_counts_when_ready()
    {
        Assert.True(RecycleBin.Eligible(DriveType.Fixed, () => true));
        Assert.False(RecycleBin.Eligible(DriveType.Removable, () => false));
    }
}
