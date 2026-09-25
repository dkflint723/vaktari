using System.Diagnostics;
using System.Runtime.Versioning;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// A share's server dies with the Vaktari that started it.
///
/// **A share went on serving after Vaktari crashed or was killed.** Windows
/// never ends a child with its parent, and only the last window closing
/// normally stopped a server — so a crash, a kill from Task Manager or a
/// logoff left a folder on the network with nobody left to take it down. The
/// server now goes into a job that Windows ends when its handle closes, and
/// the only handle is this process's. Closing it here is what the process
/// dying does to it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CopypartyJobTests
{
    [Fact]
    public void A_server_ends_when_the_job_it_was_put_in_closes()
    {
        var info = new ProcessStartInfo("ping")
        {
            ArgumentList = { "-n", "60", "127.0.0.1" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        using var server = Process.Start(info)!;

        try
        {
            var backend = new WindowsCopyparty();
            backend.Contain(server);

            Assert.NotNull(backend.Job);
            Assert.False(server.HasExited);

            backend.Job.Dispose();

            Assert.True(server.WaitForExit(5_000), "the server outlived the process that started it");
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }
}
