using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// The 8.3 short spelling of a path that exists, on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShortNames
{
    /// <summary>The short spelling of <paramref name="path"/>, or null when it
    /// is not there (or the volume will not say).</summary>
    public static string? Of(string path)
    {
        var buffer = new char[path.Length + 16];

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var length = GetShortPathNameW(path, buffer, (uint)buffer.Length);

            if (length == 0) return null;
            if (length < buffer.Length) return new string(buffer, 0, (int)length);

            buffer = new char[length];
        }

        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint size);
}
