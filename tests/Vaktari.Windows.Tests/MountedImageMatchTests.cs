using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Which file a mounted disc image is.
///
/// **By the tail alone, any file ending the same way matched.** Windows names
/// a mounted image without its drive, so D:\ISO\x.iso read as mounted when
/// C:\ISO\x.iso was, and so did C:\Downloads\ubuntu.iso for a mounted
/// \ubuntu.iso: Unmount was offered for a file that was not mounted.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MountedImageMatchTests
{
    private const string VolumeC = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
    private const string VolumeD = @"\\?\Volume{22222222-2222-2222-2222-222222222222}\";

    [WindowsFact]
    public void A_longer_path_ending_the_same_way_is_not_the_image()
        => Assert.False(WindowsDiskImages.Matches(@"C:\Downloads\ubuntu.iso", null, VolumeC, VolumeC, @"\ubuntu.iso"));

    [WindowsFact]
    public void The_same_path_on_another_volume_is_not_the_image()
        => Assert.False(WindowsDiskImages.Matches(@"D:\ISO\x.iso", null, VolumeD, VolumeC, @"\ISO\x.iso"));

    [WindowsFact]
    public void The_image_itself_is()
        => Assert.True(WindowsDiskImages.Matches(@"C:\ISO\x.iso", null, VolumeC, VolumeC.ToUpperInvariant(), @"\iso\X.ISO"));

    /// <summary>A volume that cannot be named is not a reason to say "not
    /// this one" — that answer offers Mount, and a second attach.</summary>
    [WindowsFact]
    public void An_unknown_volume_leaves_the_path_to_decide()
        => Assert.True(WindowsDiskImages.Matches(@"C:\ISO\x.iso", null, null, VolumeC, @"\ISO\x.iso"));

    /// <summary>
    /// **A volume mounted in a folder names its image from that folder.**
    /// Cut at the drive's root, C:\mnt\data\x.iso never matched \x.iso, and
    /// Mount was offered for an image already mounted.
    /// </summary>
    [WindowsFact]
    public void An_image_on_a_folder_mounted_volume_is_the_image()
        => Assert.True(WindowsDiskImages.Matches(@"C:\mnt\data\x.iso", @"C:\mnt\data\", VolumeC, VolumeC, @"\x.iso"));

    /// <summary>And on a share, whose root has no separator after it.</summary>
    [WindowsFact]
    public void An_image_on_a_share_is_the_image()
    {
        Assert.True(WindowsDiskImages.Matches(@"\\server\share\ISO\x.iso", @"\\server\share\", null, null, @"\ISO\x.iso"));
        Assert.True(WindowsDiskImages.Matches(@"\\server\share\ISO\x.iso", null, null, null, @"\ISO\x.iso"));
    }
}
