using System.Diagnostics;

namespace Vaktari.Tests;

/// <summary>
/// Another program holding something open: Vaktari.LockHolder, started by a
/// test and stopped by the same test.
///
/// **A separate process, because the question is about one.** The folder
/// hand-over lets go of everything VAKTARI holds, so a handle the test
/// process held itself would be let go of, or not, for reasons that have
/// nothing to do with another program having the file open. Measured in the
/// rename notes with a separate holder too (plan §1).
///
/// Linked into each test project that needs it, like PlatformFacts. Stops
/// only the process it started: closing its standard input ends it, and a
/// kill is the fallback for that one process alone, never anything found by
/// name.
/// </summary>
internal sealed class AnotherProgram : IDisposable
{
    private readonly Process _process;

    private AnotherProgram(Process process) => _process = process;

    /// <summary>Holds <paramref name="file"/> open with no sharing at all,
    /// creating it when it is not there.</summary>
    public static AnotherProgram HoldingFile(string file) => Start("file", file);

    /// <summary>Sits with <paramref name="folder"/> as its current folder, as
    /// a terminal left open in it does.</summary>
    public static AnotherProgram InFolder(string folder) => Start("cwd", folder);

    private static AnotherProgram Start(string mode, string path)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "Vaktari.LockHolder.dll");

        if (!File.Exists(helper))
            throw new FileNotFoundException("the lock helper was not built beside the tests", helper);

        var ready = Path.Combine(Path.GetTempPath(), "vaktari-holder-" + Guid.NewGuid().ToString("N") + ".ready");

        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Not the folder under test: the helper's own folder, so a "file"
            // holder is not also holding the folder by being in it.
            WorkingDirectory = AppContext.BaseDirectory,
        };

        info.ArgumentList.Add(helper);
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(path);
        info.ArgumentList.Add(ready);

        var process = Process.Start(info) ?? throw new InvalidOperationException("the lock helper did not start");
        var holder = new AnotherProgram(process);

        // On the ready file the helper writes once the hold is in place,
        // never on a sleep. Thirty seconds, for a loaded runner starting a
        // second runtime.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (!File.Exists(ready))
        {
            if (process.HasExited || DateTime.UtcNow > until)
            {
                holder.Dispose();
                throw new InvalidOperationException(
                    $"the lock helper never said it was holding {path} (exited: {process.HasExited})");
            }

            Thread.Sleep(20);
        }

        try { File.Delete(ready); }
        catch (IOException) { /* the helper may still have it; it is a temp file */ }

        return holder;
    }

    /// <summary>The helper's process id, which a test can check is still
    /// running — the holder must not have been closed by anything.</summary>
    public bool IsRunning => !_process.HasExited;

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();

                if (!_process.WaitForExit(10_000)) _process.Kill();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            // Already gone: nothing of ours to stop.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
