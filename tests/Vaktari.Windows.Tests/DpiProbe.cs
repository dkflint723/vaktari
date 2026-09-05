using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;
using Xunit.Abstractions;

namespace Vaktari.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class DpiProbe(ITestOutputHelper output)
{
    [WindowsFact]
    public void What_the_shell_returns_per_requested_size()
    {
        var icons = new WindowsFileIcons();
        var path = System.IO.Path.Combine(System.Environment.SystemDirectory, "notepad.exe");

        foreach (var asked in new[] { 16, 32, 40, 48, 64, 96 })
        {
            var pixels = icons.IconFor(path, isDirectory: false, asked);
            output.WriteLine($"asked {asked} -> {(pixels is null ? "null" : $"{pixels.Width}x{pixels.Height}")}");
        }

        Assert.True(true);
    }
}
