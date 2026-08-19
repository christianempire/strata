using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Strata.Core.Model;
using Strata.Platform.Native;

namespace Strata.Platform;

/// <summary>How a volume handle was opened for a probe attempt.</summary>
public enum UsnHandleStyle
{
    /// <summary>\\.\C: with no access rights requested at all.</summary>
    VolumeNoAccess,

    /// <summary>\\.\C: with FILE_READ_ATTRIBUTES.</summary>
    VolumeReadAttributes,

    /// <summary>\\.\C: with GENERIC_READ. The classic form, and the one that needs elevation.</summary>
    VolumeGenericRead,

    /// <summary>C:\ opened as a directory with backup semantics.</summary>
    RootDirectory,
}

/// <summary>Result of one probe attempt.</summary>
public readonly record struct UsnProbeAttempt(
    UsnHandleStyle Style,
    bool HandleOpened,
    bool QuerySucceeded,
    bool PrivilegedReadSucceeded,
    bool UnprivilegedReadSucceeded,
    int LastError,
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    int RecordsReturned,
    int RecordVersion);

/// <summary>
/// Establishes what the change journal will actually let us do on this machine, right now.
/// </summary>
/// <remarks>
/// This exists because the elevation question could not be settled from documentation with enough
/// confidence to design around. <c>FSCTL_READ_USN_JOURNAL</c> is documented as needing a volume
/// handle with GENERIC_READ, which needs administrator.
/// <c>FSCTL_READ_UNPRIVILEGED_USN_JOURNAL</c> exists specifically to lift that, but which handle
/// rights it needs, and whether <c>FSCTL_QUERY_USN_JOURNAL</c> has the same relaxation, are not
/// clearly specified anywhere. Rather than guess, the code tries every combination and reports.
/// <para>
/// Surfaced as <c>strata usn-probe</c> so the answer is reproducible on any machine rather than
/// being a claim in a document.
/// </para>
/// </remarks>
public static class UsnAccessProbe
{
    public static IReadOnlyList<UsnProbeAttempt> Probe(VolumeIdentity volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var attempts = new List<UsnProbeAttempt>();

        foreach (var style in Enum.GetValues<UsnHandleStyle>())
        {
            attempts.Add(Attempt(volume, style));
        }

        return attempts;
    }

    private static UsnProbeAttempt Attempt(VolumeIdentity volume, UsnHandleStyle style)
    {
        using var handle = OpenForStyle(volume, style);

        if (handle.IsInvalid)
        {
            return new UsnProbeAttempt(style, false, false, false, false,
                Marshal.GetLastPInvokeError(), 0, 0, 0, 0, 0);
        }

        var queried = TryQuery(handle, out var journal, out var queryError);

        if (!queried)
        {
            return new UsnProbeAttempt(style, true, false, false, false, queryError, 0, 0, 0, 0, 0);
        }

        var privileged = TryRead(handle, journal, Usn.FSCTL_READ_USN_JOURNAL, out var privRecords, out var privVersion);
        var unprivileged = TryRead(handle, journal, Usn.FSCTL_READ_UNPRIVILEGED_USN_JOURNAL, out var unprivRecords, out var unprivVersion);

        return new UsnProbeAttempt(
            style,
            HandleOpened: true,
            QuerySucceeded: true,
            PrivilegedReadSucceeded: privileged,
            UnprivilegedReadSucceeded: unprivileged,
            LastError: 0,
            JournalId: journal.UsnJournalID,
            FirstUsn: journal.FirstUsn,
            NextUsn: journal.NextUsn,
            RecordsReturned: Math.Max(privRecords, unprivRecords),
            RecordVersion: privVersion != 0 ? privVersion : unprivVersion);
    }

    private static SafeFileHandle OpenForStyle(VolumeIdentity volume, UsnHandleStyle style)
    {
        // \\.\C: addresses the volume itself. The trailing backslash must not be present.
        var volumeDevice = volume.DriveLetter is not null
            ? @"\\.\" + volume.DriveLetter
            : volume.GuidPath.TrimEnd('\\');

        return style switch
        {
            UsnHandleStyle.VolumeNoAccess => Kernel32.CreateFile(
                volumeDevice, 0,
                Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
                0, Kernel32.OPEN_EXISTING, 0, 0),

            UsnHandleStyle.VolumeReadAttributes => Kernel32.CreateFile(
                volumeDevice, Kernel32.FILE_READ_ATTRIBUTES | Kernel32.SYNCHRONIZE,
                Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
                0, Kernel32.OPEN_EXISTING, 0, 0),

            UsnHandleStyle.VolumeGenericRead => Kernel32.CreateFile(
                volumeDevice, Kernel32.GENERIC_READ,
                Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
                0, Kernel32.OPEN_EXISTING, 0, 0),

            _ => Kernel32.CreateFile(
                volume.RootPath, Kernel32.FILE_LIST_DIRECTORY | Kernel32.FILE_READ_ATTRIBUTES | Kernel32.SYNCHRONIZE,
                Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE,
                0, Kernel32.OPEN_EXISTING, Kernel32.FILE_FLAG_BACKUP_SEMANTICS, 0),
        };
    }

    private static unsafe bool TryQuery(SafeFileHandle handle, out USN_JOURNAL_DATA_V0 journal, out int error)
    {
        journal = default;

        fixed (USN_JOURNAL_DATA_V0* output = &journal)
        {
            var ok = Kernel32.DeviceIoControl(
                handle,
                Usn.FSCTL_QUERY_USN_JOURNAL,
                null, 0,
                output, (uint)sizeof(USN_JOURNAL_DATA_V0),
                out _, 0);

            error = ok ? 0 : Marshal.GetLastPInvokeError();
            return ok;
        }
    }

    private static unsafe bool TryRead(
        SafeFileHandle handle,
        USN_JOURNAL_DATA_V0 journal,
        uint controlCode,
        out int recordCount,
        out int recordVersion)
    {
        recordCount = 0;
        recordVersion = 0;

        var request = new READ_USN_JOURNAL_DATA_V1
        {
            // Deliberately start at the beginning of the journal so the probe returns something
            // even on a quiet machine.
            StartUsn = journal.FirstUsn,
            ReasonMask = 0xFFFFFFFF,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalID = journal.UsnJournalID,
            MinMajorVersion = 2,
            MaxMajorVersion = 3,
        };

        var buffer = new byte[64 * 1024];

        fixed (byte* output = buffer)
        {
            var ok = Kernel32.DeviceIoControl(
                handle,
                controlCode,
                &request, (uint)sizeof(READ_USN_JOURNAL_DATA_V1),
                output, (uint)buffer.Length,
                out var returned, 0);

            if (!ok)
            {
                return false;
            }

            // The first eight bytes are the next USN to resume from; records follow.
            var offset = sizeof(long);

            while (offset + 4 <= (int)returned)
            {
                var length = (int)MemoryMarshal.Read<uint>(buffer.AsSpan(offset));

                if (length <= 0 || offset + length > (int)returned)
                {
                    break;
                }

                if (recordVersion == 0)
                {
                    recordVersion = MemoryMarshal.Read<ushort>(buffer.AsSpan(offset + Usn.MajorVersion));
                }

                recordCount++;
                offset += length;
            }

            return true;
        }
    }
}
