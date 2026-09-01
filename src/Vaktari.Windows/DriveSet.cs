using Vaktari.Core;

namespace Vaktari.Windows;

/// <summary>
/// One line of text describing every local volume, cheap enough to ask for
/// every second.
///
/// **What this is allowed to read is the whole design.** Name, DriveType and
/// IsReady — nothing else, ever. Not the volume label, not the size, not the
/// free space. Those are the calls that block:
/// <c>SidebarViewModel.ReloadAsync</c> documents a window frozen before it had
/// drawn, because a mapped drive whose server was gone answered its size query
/// only after the SMB timeout. That was a one-off freeze at startup. In a loop
/// it would be a machine that hangs for a moment, forever.
///
/// So Network and NoRootDirectory are skipped before anything is asked of them
/// — <c>DriveType</c> is <c>GetDriveType</c>, which reads the DOS device map
/// and never touches a wire, making it safe to ask FIRST and decide from.
///
/// Readiness is IN the key rather than merely filtered on, because a card
/// reader and an empty optical bay keep their drive letters with no media in
/// them. A key made of letters alone is structurally blind to a card being
/// pushed in — the exact event this exists to notice. Free space is deliberately
/// OUT of the key: it changes constantly, and a watcher keyed on it would
/// announce a change every second of a large copy.
/// </summary>
internal static class DriveSet
{
    /// <summary>
    /// The real snapshot, over this machine's drives.
    /// </summary>
    internal static string Snapshot()
        => Snapshot(DriveInfo.GetDrives().Select(drive => (
            drive.Name,
            drive.DriveType,
            Ready: (Func<bool>)(() =>
            {
                try
                {
                    return drive.IsReady;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A drive that will not say is not ready, which is also how
                    // BuildDrives already treats it.
                    return false;
                }
            }))));

    /// <summary>
    /// The decision, over drives described rather than discovered — which is
    /// what lets the rules below be tested without owning the hardware they
    /// describe.
    /// </summary>
    internal static string Snapshot(
        IEnumerable<(string Name, DriveType Type, Func<bool> Ready)> drives)
    {
        var lines = new List<string>();

        foreach (var drive in drives)
        {
            // **Never ask a network drive anything.** Not even whether it is
            // ready: that is the call that blocks for the SMB timeout. A share
            // appearing or going away is not what this watches for, and the
            // sidebar rebuild it would trigger is the very work that freezes.
            if (drive.Type is DriveType.Network or DriveType.NoRootDirectory) continue;

            bool ready;

            try
            {
                ready = drive.Ready();
            }
            catch (Exception ex)
            {
                // Someone else's callback threw. Not ready, and not fatal.
                Quiet.Swallowed("places", ex);
                ready = false;
            }

            lines.Add($"{drive.Name}|{(int)drive.Type}|{(ready ? 1 : 0)}");
        }

        // Sorted so that the enumeration order — which is not promised to be
        // stable — cannot masquerade as a change, and joined into one string
        // because string equality is the one comparison nobody gets wrong, and
        // it prints the whole difference when a test fails.
        lines.Sort(StringComparer.Ordinal);

        return string.Join("\n", lines);
    }
}
