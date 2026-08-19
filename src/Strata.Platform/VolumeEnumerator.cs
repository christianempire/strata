using System.Runtime.InteropServices;
using Strata.Core.Model;
using Strata.Platform.Native;

namespace Strata.Platform;

/// <summary>
/// Discovers the volumes attached to this machine.
/// </summary>
/// <remarks>
/// Enumerates by volume GUID rather than by drive letter, which is both how unlettered volumes
/// (recovery partitions, mounted folders) get found and how volume identity stays stable when
/// letters are reassigned.
/// </remarks>
public static class VolumeEnumerator
{
    private const int VolumeNameBufferChars = 64; // \\?\Volume{...}\ is 49 characters
    private const int PathNamesBufferChars = 1024;
    private const int LabelBufferChars = 261;
    private const int FileSystemBufferChars = 32;

    /// <summary>
    /// Returns every volume the OS reports, including ones Strata will not scan. Filtering is the
    /// caller's decision, so that the CLI and the UI can show a volume alongside the reason it is
    /// being skipped rather than silently omitting it.
    /// </summary>
    public static IReadOnlyList<VolumeIdentity> Enumerate()
    {
        var volumes = new List<VolumeIdentity>();
        var nameBuffer = new char[VolumeNameBufferChars];

        using var find = Kernel32.FindFirstVolume(nameBuffer, VolumeNameBufferChars);

        if (find.IsInvalid)
        {
            throw new InvalidOperationException(
                $"FindFirstVolume failed with error {Marshal.GetLastPInvokeError()}.");
        }

        while (true)
        {
            var guidPath = TrimBuffer(nameBuffer);

            if (guidPath.Length > 0)
            {
                volumes.Add(Describe(guidPath));
            }

            if (!Kernel32.FindNextVolume(find, nameBuffer, VolumeNameBufferChars))
            {
                var error = Marshal.GetLastPInvokeError();

                if (error is Kernel32.ERROR_NO_MORE_FILES or Kernel32.ERROR_NO_MORE_ITEMS)
                {
                    break;
                }

                throw new InvalidOperationException($"FindNextVolume failed with error {error}.");
            }
        }

        return volumes;
    }

    private static VolumeIdentity Describe(string guidPath)
    {
        var driveLetter = FirstDriveLetter(guidPath);
        var kind = ToKind(Kernel32.GetDriveType(guidPath));

        string label = "";
        string fileSystem = "";
        uint serial = 0;

        var labelBuffer = new char[LabelBufferChars];
        var fileSystemBuffer = new char[FileSystemBufferChars];

        if (Kernel32.GetVolumeInformation(
                guidPath,
                labelBuffer,
                LabelBufferChars,
                out serial,
                out _,
                out _,
                fileSystemBuffer,
                FileSystemBufferChars))
        {
            label = TrimBuffer(labelBuffer);
            fileSystem = TrimBuffer(fileSystemBuffer);
        }

        long totalBytes = 0;
        long freeBytes = 0;

        // Fails for a drive with no media, which is expected rather than exceptional.
        if (Kernel32.GetDiskFreeSpaceEx(guidPath, out _, out var total, out var free))
        {
            totalBytes = (long)total;
            freeBytes = (long)free;
        }

        return new VolumeIdentity
        {
            GuidPath = guidPath,
            DriveLetter = driveLetter,
            Label = label,
            FileSystem = fileSystem,
            Kind = kind,
            SerialNumber = serial,
            TotalBytes = totalBytes,
            FreeBytes = freeBytes,
            DiskNumber = TryGetDiskNumber(driveLetter),
        };
    }

    /// <summary>
    /// Returns the first drive letter mounted at this volume, or null if it has none.
    /// </summary>
    /// <remarks>
    /// A volume can be mounted at several paths at once; only the letter is kept, because it is a
    /// display attribute. Identity is the GUID.
    /// </remarks>
    private static string? FirstDriveLetter(string guidPath)
    {
        var buffer = new char[PathNamesBufferChars];

        if (!Kernel32.GetVolumePathNamesForVolumeName(
                guidPath,
                buffer,
                PathNamesBufferChars,
                out var returned) || returned == 0)
        {
            return null;
        }

        // A double-null-terminated list of paths. Only "X:\" shapes are drive letters; anything
        // else is a mounted folder.
        var span = buffer.AsSpan(0, Math.Min((int)returned, buffer.Length));

        foreach (var range in span.Split('\0'))
        {
            var candidate = span[range];

            if (candidate.Length == 3 && char.IsAsciiLetter(candidate[0]) && candidate[1] == ':')
            {
                return candidate[..2].ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves which physical disk backs a volume, so the UI can group volumes by drive.
    /// </summary>
    /// <remarks>
    /// Opening \\.\X: with no access rights at all is enough for this IOCTL and needs no elevation.
    /// Best-effort: a null here costs a grouping nicety, not a scan.
    /// </remarks>
    private static unsafe int? TryGetDiskNumber(string? driveLetter)
    {
        if (driveLetter is null)
        {
            return null;
        }

        using var handle = Kernel32.CreateFile(
            @"\\.\" + driveLetter,
            dwDesiredAccess: 0,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
            lpSecurityAttributes: 0,
            Kernel32.OPEN_EXISTING,
            dwFlagsAndAttributes: 0,
            hTemplateFile: 0);

        if (handle.IsInvalid)
        {
            return null;
        }

        STORAGE_DEVICE_NUMBER number;

        var ok = Kernel32.DeviceIoControl(
            handle,
            Kernel32.IOCTL_STORAGE_GET_DEVICE_NUMBER,
            lpInBuffer: null,
            nInBufferSize: 0,
            &number,
            (uint)sizeof(STORAGE_DEVICE_NUMBER),
            out _,
            lpOverlapped: 0);

        return ok ? (int)number.DeviceNumber : null;
    }

    private static VolumeKind ToKind(uint driveType)
        => driveType switch
        {
            Kernel32.DRIVE_FIXED => VolumeKind.Fixed,
            Kernel32.DRIVE_REMOVABLE => VolumeKind.Removable,
            Kernel32.DRIVE_REMOTE => VolumeKind.Network,
            Kernel32.DRIVE_CDROM => VolumeKind.CdRom,
            Kernel32.DRIVE_RAMDISK => VolumeKind.RamDisk,
            _ => VolumeKind.Unknown,
        };

    private static string TrimBuffer(char[] buffer)
    {
        var end = Array.IndexOf(buffer, '\0');
        return end < 0 ? new string(buffer) : new string(buffer, 0, end);
    }
}
