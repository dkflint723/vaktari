using System.Runtime.Versioning;
using Vaktari.Core.Places;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What the provider refuses before any device is touched.
///
/// **These are call-count assertions, not message assertions**, and that is the
/// point: "the system disk is never handed to the ejector" has to be a fact
/// about the call graph. A guard that lived inside the ejector would be one
/// refactor away from being bypassed, and the failure mode is a machine being
/// asked to tear out the volume it is running from.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PlacesEjectGuardTests
{
    private sealed class CountingEjector : IEjector
    {
        public int Calls { get; private set; }

        public Task<EjectResult> EjectAsync(string path, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(EjectResult.Ejected("ejected"));
        }
    }

    private static (WindowsPlacesProvider Provider, CountingEjector Ejector) Fresh()
    {
        var state = Directory.CreateTempSubdirectory("vaktari-eject-guard").FullName;
        var ejector = new CountingEjector();

        return (new WindowsPlacesProvider(state) { EjectorOverride = ejector }, ejector);
    }

    /// <summary>
    /// The system drive is on every machine this test runs on, and it is never
    /// removable — so the device layer must never see it.
    /// </summary>
    [Fact]
    public async Task A_fixed_disk_is_refused_without_the_device_layer_being_asked()
    {
        var (provider, ejector) = Fresh();

        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        var result = await provider.EjectAsync("dev:" + system, CancellationToken.None);

        Assert.Equal(EjectOutcome.NotRemovable, result.Outcome);
        Assert.Equal(0, ejector.Calls);
    }

    /// <summary>
    /// An id naming a drive that is no longer there — the row was clicked just
    /// as the stick was pulled — is answered, not thrown.
    /// </summary>
    [Fact]
    public async Task An_id_that_names_nothing_is_refused_quietly()
    {
        var (provider, ejector) = Fresh();

        var result = await provider.EjectAsync("dev:Ω:\\", CancellationToken.None);

        Assert.Equal(EjectOutcome.NotRemovable, result.Outcome);
        Assert.Equal(0, ejector.Calls);
    }

    /// <summary>
    /// **Only a real ejection rebuilds the sidebar.** A row that vanished after
    /// a vetoed eject would tell the person the drive is gone while it is still
    /// mounted and still being written to.
    /// </summary>
    [Fact]
    public async Task A_refusal_does_not_announce_that_the_drives_changed()
    {
        var state = Directory.CreateTempSubdirectory("vaktari-eject-guard").FullName;
        var raised = 0;

        var provider = new WindowsPlacesProvider(state)
        {
            EjectorOverride = new CountingEjector(),
        };

        provider.PlacesChanged += (_, _) => raised++;

        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        await provider.EjectAsync("dev:" + system, CancellationToken.None);

        Assert.Equal(0, raised);
    }
}
