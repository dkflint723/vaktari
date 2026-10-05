using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The Windows half of Core's StreamlineQaDeviceWatchTests (QA,
/// qa/streamline). The watch counts a fallback change as missed when the
/// ANNOUNCED parts of the two looks differ; this pins that the announced part
/// the started provider hands its watch still differs when a removable drive
/// arrives beside a mapped letter, so the letter's exemption cannot swallow
/// the stick.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StreamlineQaDriveSetTests
{
    [WindowsFact]
    public void A_stick_arriving_beside_a_mapped_letter_still_changes_the_announced_part()
    {
        var before = DriveSet.Snapshot(
        [
            ("C:\\", DriveType.Fixed, () => true),
        ]);

        var now = DriveSet.Snapshot(
        [
            ("C:\\", DriveType.Fixed, () => true),
            ("E:\\", DriveType.Removable, () => true),
            ("Z:\\", DriveType.Network, () => true),
        ]);

        var state = Directory.CreateTempSubdirectory("vaktari-qa-announced").FullName;

        try
        {
            using var places = new WindowsPlacesProvider(state);
            places.Start();

            var announced = places.WatchAnnouncedForTests;
            Assert.NotNull(announced);

            Assert.NotEqual(announced(before), announced(now));
            Assert.Equal(announced(before), announced(before + "\nZ:\\|4|1"));
        }
        finally
        {
            Directory.Delete(state, recursive: true);
        }
    }
}
