using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vaktari.Core;
using Vaktari.Core.Places;

namespace Vaktari.Windows;

/// <summary>
/// Mounting an .iso through the Virtual Disk Service, which is the same
/// machinery behind Explorer's own Mount verb.
///
/// **Measured unelevated before it was written**, on Windows 11 build 26200
/// with a normal account: OpenVirtualDisk, AttachVirtualDisk, a new drive
/// letter, and DetachVirtualDisk all returned success. That mattered enough to
/// check rather than assume, because Vaktari never holds administrator rights
/// and a verb that silently needs them is a verb that silently fails.
///
/// VHD and VHDX are the counter-example and are deliberately NOT offered:
/// attaching one returns ERROR_PRIVILEGE_NOT_HELD without elevation, with or
/// without a permanent lifetime. Offering a Mount entry that always fails for
/// a whole file type would be worse than offering nothing.
///
/// The API rather than the shell verb, even though the shell verb exists and
/// this project already hosts IContextMenu: the verb reports no result and no
/// failure reason, so a corrupt image would look exactly like a successful
/// mount that produced no drive.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDiskImages : IDiskImages
{
    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    /// <summary>
    /// The extensions Windows' ISO provider actually handles.
    ///
    /// .img is here because Windows itself registers it to Windows.IsoFile, and
    /// a real ISO renamed .img was measured mounting through the ISO provider.
    /// A .img that is NOT an image fails cleanly at OpenVirtualDisk, before any
    /// device exists — so the gate can afford to be generous here.
    /// </summary>
    private static readonly HashSet<string> Mountable =
        new(StringComparer.OrdinalIgnoreCase) { ".iso", ".img", ".udf" };

    /// <summary>
    /// **Name only — no disk is touched.** This is asked for every item a menu
    /// opens over, so a Directory.Exists here would be a stat per right-click,
    /// which is the rule FileCategories already states for exactly this reason.
    /// A folder named holiday.iso would offer a Mount that fails cleanly; a
    /// stat on every menu open is a cost paid always.
    /// </summary>
    public bool CanMount(string path)
        => !string.IsNullOrEmpty(path) && Mountable.Contains(Path.GetExtension(path));

    /// <summary>
    /// Where this image is mounted — **asked of Windows, not remembered.**
    ///
    /// A process-local map was the obvious design and is wrong twice over. A
    /// permanent attachment outlives Vaktari, so after a restart the map says
    /// "not mounted" about an image that plainly is — and acting on that answer
    /// attaches it a SECOND time, which was measured producing a second drive
    /// letter for one file. It also cannot see an image mounted from Explorer.
    ///
    /// So each mounted volume is asked what it came from. A volume that is not
    /// a virtual disk answers NOT_VIRTUAL_DISK immediately, which makes this
    /// cheap enough for a menu that only asks about image files anyway.
    /// </summary>
    public MountedImage? MountOf(string imagePath)
    {
        var full = Full(imagePath);

        foreach (var drive in DriveInfo.GetDrives())
        {
            // Only optical-presenting volumes can be a mounted image, and never
            // a network drive — asking one anything is the call that blocks.
            if (drive.DriveType is not DriveType.CDRom) continue;

            var letter = drive.Name.TrimEnd(Path.DirectorySeparatorChar);

            if (BackingImageOf($@"\\.\{letter}") is not { } backing) continue;

            // The dependency names the file WITHOUT its drive, so the tail is
            // what can be compared. Rooted at the volume the image lives on.
            if (full.EndsWith(backing, StringComparison.OrdinalIgnoreCase))
                return new MountedImage(full, drive.Name);
        }

        return null;
    }

    /// <summary>
    /// The image file behind a mounted volume, as Windows reports it —
    /// "\Program Files (x86)\...\windows-x86.iso", drive letter absent.
    /// Null when the volume is not a mounted image at all.
    /// </summary>
    private static string? BackingImageOf(string devicePath)
    {
        var handle = Native.CreateFile(
            devicePath, 0,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            0, Native.OPEN_EXISTING, 0, 0);

        if (handle == Native.INVALID_HANDLE_VALUE) return null;

        try
        {
            var size = (uint)Marshal.SizeOf<Native.STORAGE_DEPENDENCY_INFO_V2>() + 512;
            var buffer = Marshal.AllocHGlobal((int)size);

            try
            {
                Marshal.WriteInt32(buffer, (int)Native.STORAGE_DEPENDENCY_INFO_VERSION_2);
                Marshal.WriteInt32(buffer, 4, 0);

                var asked = Native.GetStorageDependencyInformation(
                    handle,
                    Native.GET_STORAGE_DEPENDENCY_FLAG_HOST_VOLUMES
                    | Native.GET_STORAGE_DEPENDENCY_FLAG_DISK_HANDLE,
                    size, buffer, out _);

                if (asked != Native.ERROR_SUCCESS) return null;

                var info = Marshal.PtrToStructure<Native.STORAGE_DEPENDENCY_INFO_V2>(buffer);

                if (info.NumberEntries == 0) return null;

                return info.FirstEntry.DependentVolumeRelativePath == 0
                    ? null
                    : Marshal.PtrToStringUni(info.FirstEntry.DependentVolumeRelativePath);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("places", ex);
            return null;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    public Task<MountedImage> MountAsync(string imagePath, CancellationToken ct)
        => Task.Run(() => Mount(imagePath), ct);

    private MountedImage Mount(string imagePath)
    {
        var full = Full(imagePath);

        if (!File.Exists(full)) throw new FileNotFoundException("that image is not there", full);

        // **Never attach the same image twice.** Measured: a second attach
        // succeeds and produces a SECOND drive letter for one file, and then
        // each detach removes only one attachment — so the duplicate outlives
        // an unmount and looks like the unmount failed.
        if (MountOf(full) is { } already) return already;

        var handle = Open(full, Native.VIRTUAL_DISK_ACCESS_READ);
        string? mountPath;

        try
        {
            var attached = Native.AttachVirtualDisk(
                handle, 0,
                Native.ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY
                | Native.ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME,
                0, 0, 0);

            if (attached != Native.ERROR_SUCCESS) throw Complain(attached, full);

            // **Asked for by identity, not spotted by difference.** Watching
            // for "a drive letter that was not there a moment ago" is the
            // obvious version and answers the wrong question: it cannot tell
            // OUR image's letter from one that appeared because somebody
            // plugged a stick in while the mount was running — which the new
            // arrival watcher makes a live possibility rather than a theory.
            // The device the image attached as is unambiguous.
            mountPath = WaitForVolume(PhysicalDevice(handle));
        }
        finally
        {
            // Closed once the letter is known: with a permanent lifetime the
            // image stays attached without us, and holding the handle would make
            // Vaktari the thing stopping anyone else from detaching it.
            Native.CloseHandle(handle);
        }

        if (mountPath is null)
            throw new IOException(
                $"{Path.GetFileName(full)} attached, but Windows gave it no drive letter — "
                + "it may hold no filesystem Windows can read");

        var mount = new MountedImage(full, mountPath);


        return mount;
    }

    public Task UnmountAsync(string imagePath, CancellationToken ct)
        => Task.Run(() => Unmount(imagePath), ct);

    private void Unmount(string imagePath)
    {
        var full = Full(imagePath);
        var handle = Open(full, Native.VIRTUAL_DISK_ACCESS_DETACH);

        try
        {
            var detached = Native.DetachVirtualDisk(handle, Native.DETACH_VIRTUAL_DISK_FLAG_NONE, 0);

            if (detached != Native.ERROR_SUCCESS) throw Complain(detached, full);
        }
        finally
        {
            Native.CloseHandle(handle);
        }

    }

    private static nint Open(string path, uint access)
    {
        var type = new Native.VIRTUAL_STORAGE_TYPE
        {
            DeviceId = Native.VIRTUAL_STORAGE_TYPE_DEVICE_ISO,
            VendorId = Native.VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT,
        };

        var opened = Native.OpenVirtualDisk(
            in type, path, access, Native.OPEN_VIRTUAL_DISK_FLAG_NONE, 0, out var handle);

        if (opened != Native.ERROR_SUCCESS) throw Complain(opened, path);

        return handle;
    }

    /// <summary>
    /// The system's error, as a sentence about this file.
    ///
    /// Every one of these is a thing a person can act on — the file is not an
    /// image, or it is a type Windows will not attach without rights Vaktari
    /// does not have — so none of them should reach the status bar as a number.
    /// </summary>
    private static IOException Complain(int code, string path) => code switch
    {
        Native.ERROR_FILE_CORRUPT or Native.ERROR_VIRTDISK_PROVIDER_NOT_FOUND =>
            new IOException($"{Path.GetFileName(path)} is not a disk image Windows can open"),

        Native.ERROR_PRIVILEGE_NOT_HELD =>
            new IOException(
                $"Windows will not mount {Path.GetFileName(path)} without administrator rights"),

        Native.ERROR_NOT_READY =>
            new IOException($"{Path.GetFileName(path)} is not ready"),

        _ => new IOException(
            $"Windows would not mount {Path.GetFileName(path)} (error {code})"),
    };

    /// <summary>
    /// The device the attached image presents as — "\\.\CDROM0" — which is not
    /// somewhere anyone can navigate, but IS the identity that maps to a letter.
    /// </summary>
    private static string? PhysicalDevice(nint handle)
    {
        // Bytes, not characters, and the call is asked twice: once to be told
        // the size, once to fill it.
        uint size = 0;

        Native.GetVirtualDiskPhysicalPath(handle, ref size, 0);

        if (size == 0) size = 260 * sizeof(char);

        var buffer = Marshal.AllocHGlobal((int)size);

        try
        {
            return Native.GetVirtualDiskPhysicalPath(handle, ref size, buffer)
                    == Native.ERROR_SUCCESS
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The drive letter sitting on that device, once it is READY.
    ///
    /// **Readiness, not mere presence.** The letter appears before the
    /// filesystem behind it can be listed: measured against a real ISO,
    /// returning on first sight handed back a path that immediately threw
    /// "a device which does not exist was specified". A mount that answers with
    /// a path nobody can open is a mount that failed.
    ///
    /// Bounded: an image holding no filesystem Windows recognises attaches
    /// successfully and never produces a usable volume, and that is reported
    /// rather than waited on forever.
    /// </summary>
    private static string? WaitForVolume(string? devicePath)
    {
        if (devicePath is null) return null;

        var wanted = WindowsEjector.DeviceNumberOf(devicePath);

        for (var attempt = 0; attempt < 60; attempt++)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                // Never a network drive: asking one anything is the call that
                // blocks for the SMB timeout, and an image is never one.
                if (drive.DriveType == DriveType.Network) continue;

                var letter = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
                var number = WindowsEjector.DeviceNumberOf($@"\\.\{letter}");

                // **Both halves, or the system disk matches.** DeviceNumber is
                // unique only WITHIN a device type: \\.\CDROM0 and the first
                // physical disk are both number 0, so comparing the number
                // alone answered "C:\" for a mounted ISO — measured, on the
                // first real image this was pointed at.
                if (number is null || wanted is null) continue;
                if (number.Value.DeviceNumber != wanted.Value.DeviceNumber) continue;
                if (number.Value.DeviceType != wanted.Value.DeviceType) continue;

                try
                {
                    if (drive.IsReady) return drive.Name;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Not ready yet; it is the right device, so keep waiting.
                }
            }

            Thread.Sleep(100);
        }

        return null;
    }

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException
                                    or PathTooLongException)
        {
            return path;
        }
    }
}
