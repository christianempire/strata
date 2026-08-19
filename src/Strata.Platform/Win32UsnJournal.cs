using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Platform.Native;

namespace Strata.Platform;

/// <summary>
/// Reads a volume's NTFS change journal.
/// </summary>
/// <remarks>
/// Opens the volume *root directory* rather than the volume device, and issues
/// <c>FSCTL_READ_UNPRIVILEGED_USN_JOURNAL</c>. Measured on the target machine, that combination
/// works with no elevation at all, while the documented <c>\\.\C:</c> + GENERIC_READ form needs
/// administrator. Running the permanently-resident scanner unprivileged is worth a great deal, so
/// the unprivileged path is the primary one and the privileged FSCTL is only a fallback.
/// <para>
/// What still needs elevation is <see cref="TryCreateJournal"/>, one time per volume, on volumes
/// where Windows has not already enabled a journal. On the target machine that is D: and E:; the
/// system volume always has one because Search and maintenance rely on it.
/// </para>
/// </remarks>
public sealed class Win32UsnJournal : IUsnJournal, IDisposable
{
    /// <summary>
    /// Read buffer. Large enough that a quiet tick completes in one call, small enough to stay off
    /// the large object heap.
    /// </summary>
    private const int BufferSize = 64 * 1024;

    private readonly VolumeIdentity _volume;
    private readonly SafeFileHandle _handle;

    /// <summary>
    /// Set once the privileged FSCTL has been seen to work, so we stop paying for a failed
    /// unprivileged attempt on every read.
    /// </summary>
    private uint _readControlCode = Usn.FSCTL_READ_UNPRIVILEGED_USN_JOURNAL;

    private bool _disposed;

    public Win32UsnJournal(VolumeIdentity volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        _volume = volume;
        _handle = OpenRoot(volume);
    }

    /// <summary>Whether the volume could be opened at all.</summary>
    public bool IsOpen => !_handle.IsInvalid;

    public UsnJournalState? TryGetState()
    {
        if (!IsOpen)
        {
            return null;
        }

        return TryQuery(out var data) ? new UsnJournalState(data.UsnJournalID, data.FirstUsn, data.NextUsn) : null;
    }

    public UsnReadResult Read(UsnCursor cursor, int maxRecords)
    {
        if (!IsOpen)
        {
            return new UsnReadResult { Status = UsnReadStatus.AccessDenied };
        }

        if (!TryQuery(out var journal))
        {
            var error = Marshal.GetLastPInvokeError();

            return new UsnReadResult
            {
                Status = error == Kernel32.ERROR_JOURNAL_NOT_ACTIVE
                    ? UsnReadStatus.JournalNotActive
                    : UsnReadStatus.Failed,
                NativeErrorCode = error,
            };
        }

        var state = new UsnJournalState(journal.UsnJournalID, journal.FirstUsn, journal.NextUsn);

        // A cursor we cannot resume from means the incremental history has a hole in it, which is
        // worse than a delay. Say so, and let the caller fall back to a baseline.
        if (cursor.IsSet && !state.CanResumeFrom(cursor))
        {
            return new UsnReadResult { Status = UsnReadStatus.JournalInvalidated };
        }

        var start = cursor.IsSet ? cursor.NextUsn : journal.NextUsn;
        var changes = new List<UsnChange>(Math.Min(maxRecords, 4096));
        var nextUsn = start;

        while (changes.Count < maxRecords)
        {
            if (!ReadBatch(journal.UsnJournalID, nextUsn, changes, maxRecords, out var batchNext, out var error))
            {
                return new UsnReadResult
                {
                    Status = Classify(error),
                    NativeErrorCode = error,
                };
            }

            // The journal returns the position to resume from even when it produced no records,
            // which is how a tick that saw only filtered-out reasons still makes progress.
            if (batchNext == nextUsn)
            {
                break;
            }

            nextUsn = batchNext;

            if (nextUsn >= journal.NextUsn)
            {
                break;
            }
        }

        return new UsnReadResult
        {
            Status = UsnReadStatus.Ok,
            Changes = changes,
            NextUsn = nextUsn,
            JournalId = journal.UsnJournalID,
            MoreAvailable = changes.Count >= maxRecords && nextUsn < journal.NextUsn,
        };
    }

    private unsafe bool ReadBatch(
        ulong journalId,
        long startUsn,
        List<UsnChange> into,
        int maxRecords,
        out long nextUsn,
        out int error)
    {
        nextUsn = startUsn;
        error = 0;

        var request = new READ_USN_JOURNAL_DATA_V1
        {
            StartUsn = startUsn,
            ReasonMask = Usn.InterestingReasons,

            // Zero: return records as they are written rather than waiting for the file to close.
            // Waiting for close would systematically miss long-lived writers, which is exactly the
            // case that matters - a game installer holding a 40 GB file open for minutes.
            ReturnOnlyOnClose = 0,

            // Zero timeout and zero BytesToWaitFor make this a poll rather than a blocking wait.
            // A scan that blocks inside the kernel cannot honour a cancellation or a gaming pause.
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalID = journalId,
            MinMajorVersion = 2,
            MaxMajorVersion = 3,
        };

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            fixed (byte* output = buffer)
            {
                var ok = Kernel32.DeviceIoControl(
                    _handle, _readControlCode,
                    &request, (uint)sizeof(READ_USN_JOURNAL_DATA_V1),
                    output, BufferSize,
                    out var returned, 0);

                // Only retry privileged when the failure looks like "this FSCTL is not available
                // to you". Falling back on an unrelated error (no journal, bad cursor) would
                // permanently abandon the unprivileged path for no reason.
                if (!ok &&
                    _readControlCode == Usn.FSCTL_READ_UNPRIVILEGED_USN_JOURNAL &&
                    Marshal.GetLastPInvokeError() is Kernel32.ERROR_INVALID_FUNCTION or Kernel32.ERROR_ACCESS_DENIED)
                {
                    // Fall back to the privileged FSCTL once, in case this volume or Windows build
                    // does not support the unprivileged form.
                    _readControlCode = Usn.FSCTL_READ_USN_JOURNAL;

                    ok = Kernel32.DeviceIoControl(
                        _handle, _readControlCode,
                        &request, (uint)sizeof(READ_USN_JOURNAL_DATA_V1),
                        output, BufferSize,
                        out returned, 0);
                }

                if (!ok)
                {
                    error = Marshal.GetLastPInvokeError();
                    return false;
                }

                // The reply is a USN cursor followed by packed variable-length records.
                if (returned < sizeof(long))
                {
                    return true;
                }

                nextUsn = MemoryMarshal.Read<long>(buffer.AsSpan(0, sizeof(long)));
                ParseRecords(buffer.AsSpan(sizeof(long), (int)returned - sizeof(long)), into, maxRecords);
                return true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Walks the packed records, tolerating both V2 and V3 layouts in one buffer.
    /// </summary>
    /// <remarks>
    /// Measured on the target machine: under the unprivileged FSCTL every record arrives with
    /// FileNameLength of zero, in both V2 and V3. The names are redacted, not merely absent from one
    /// record version. Callers therefore cannot rely on <c>UsnChange.Name</c> being populated and
    /// must resolve paths by file ID instead; see Win32FileMetadataReader.TryGetPath.
    /// </remarks>
    private static void ParseRecords(ReadOnlySpan<byte> buffer, List<UsnChange> into, int maxRecords)
    {
        var offset = 0;

        while (offset + 8 <= buffer.Length && into.Count < maxRecords)
        {
            var record = buffer[offset..];
            var length = (int)MemoryMarshal.Read<uint>(record);

            if (length <= 0 || offset + length > buffer.Length)
            {
                return;
            }

            var major = MemoryMarshal.Read<ushort>(record[Usn.MajorVersion..]);


            if (major == 2)
            {
                AppendV2(record[..length], into);
            }
            else if (major == 3)
            {
                AppendV3(record[..length], into);
            }

            // Any other version is silently skipped rather than guessed at: misreading a record
            // would produce a plausible but wrong change, which is worse than missing one.
            offset += length;
        }
    }

    private static void AppendV2(ReadOnlySpan<byte> record, List<UsnChange> into)
    {
        var reason = MemoryMarshal.Read<uint>(record[Usn.V2Reason..]);
        var kind = ToKind(reason);

        if (kind == UsnChangeKind.None)
        {
            return;
        }

        into.Add(new UsnChange(
            MemoryMarshal.Read<long>(record[Usn.V2Usn..]),
            MemoryMarshal.Read<long>(record[Usn.V2FileReferenceNumber..]),
            MemoryMarshal.Read<long>(record[Usn.V2ParentFileReferenceNumber..]),
            ReadName(record, Usn.V2FileNameLength),
            kind,
            MemoryMarshal.Read<uint>(record[Usn.V2FileAttributes..]),
            ToDateTime(MemoryMarshal.Read<long>(record[Usn.V2TimeStamp..]))));
    }

    private static void AppendV3(ReadOnlySpan<byte> record, List<UsnChange> into)
    {
        var reason = MemoryMarshal.Read<uint>(record[Usn.V3Reason..]);
        var kind = ToKind(reason);

        if (kind == UsnChangeKind.None)
        {
            return;
        }

        into.Add(new UsnChange(
            MemoryMarshal.Read<long>(record[Usn.V3Usn..]),
            // FILE_ID_128, but on NTFS only the low 64 bits are ever populated, and those are the
            // same file reference number the directory enumeration reports. Taking the low half
            // keeps V2 and V3 records joinable against the same rows.
            MemoryMarshal.Read<long>(record[Usn.V3FileReferenceNumber..]),
            MemoryMarshal.Read<long>(record[Usn.V3ParentFileReferenceNumber..]),
            ReadName(record, Usn.V3FileNameLength),
            kind,
            MemoryMarshal.Read<uint>(record[Usn.V3FileAttributes..]),
            ToDateTime(MemoryMarshal.Read<long>(record[Usn.V3TimeStamp..]))));
    }

    /// <summary>
    /// Reads the inline name. FileNameOffset always immediately follows FileNameLength in both
    /// record versions, so one offset locates the pair.
    /// </summary>
    private static string ReadName(ReadOnlySpan<byte> record, int lengthField)
    {
        var nameLength = MemoryMarshal.Read<ushort>(record[lengthField..]);
        var nameOffset = MemoryMarshal.Read<ushort>(record[(lengthField + 2)..]);

        if (nameOffset + nameLength > record.Length)
        {
            return "";
        }

        return new string(MemoryMarshal.Cast<byte, char>(record.Slice(nameOffset, nameLength)));
    }

    private static UsnChangeKind ToKind(uint reason)
    {
        var kind = UsnChangeKind.None;

        if ((reason & Usn.USN_REASON_FILE_CREATE) != 0)
        {
            kind |= UsnChangeKind.Created;
        }

        if ((reason & Usn.USN_REASON_FILE_DELETE) != 0)
        {
            kind |= UsnChangeKind.Deleted;
        }

        const uint dataReasons =
            Usn.USN_REASON_DATA_OVERWRITE | Usn.USN_REASON_DATA_EXTEND | Usn.USN_REASON_DATA_TRUNCATION |
            Usn.USN_REASON_NAMED_DATA_OVERWRITE | Usn.USN_REASON_NAMED_DATA_EXTEND |
            Usn.USN_REASON_NAMED_DATA_TRUNCATION | Usn.USN_REASON_COMPRESSION_CHANGE |
            Usn.USN_REASON_STREAM_CHANGE | Usn.USN_REASON_REPARSE_POINT_CHANGE;

        if ((reason & dataReasons) != 0)
        {
            kind |= UsnChangeKind.DataChanged;
        }

        if ((reason & Usn.USN_REASON_RENAME_OLD_NAME) != 0)
        {
            kind |= UsnChangeKind.RenamedFrom;
        }

        if ((reason & Usn.USN_REASON_RENAME_NEW_NAME) != 0)
        {
            kind |= UsnChangeKind.RenamedTo;
        }

        if ((reason & Usn.USN_REASON_HARD_LINK_CHANGE) != 0)
        {
            kind |= UsnChangeKind.HardLinkChanged;
        }

        return kind;
    }

    /// <summary>
    /// Enables a journal on this volume. Requires elevation.
    /// </summary>
    /// <param name="maximumSize">
    /// How much history the journal retains. Larger means longer offline before a baseline is
    /// forced; the cost is disk. 32 MB holds roughly a day of ordinary activity on a busy volume.
    /// </param>
    public unsafe bool TryCreateJournal(ulong maximumSize = 32UL * 1024 * 1024, ulong allocationDelta = 4UL * 1024 * 1024)
    {
        if (!IsOpen)
        {
            return false;
        }

        var request = new CREATE_USN_JOURNAL_DATA
        {
            MaximumSize = maximumSize,
            AllocationDelta = allocationDelta,
        };

        return Kernel32.DeviceIoControl(
            _handle,
            Usn.FSCTL_CREATE_USN_JOURNAL,
            &request, (uint)sizeof(CREATE_USN_JOURNAL_DATA),
            null, 0,
            out _, 0);
    }

    private unsafe bool TryQuery(out USN_JOURNAL_DATA_V0 journal)
    {
        journal = default;

        fixed (USN_JOURNAL_DATA_V0* output = &journal)
        {
            return Kernel32.DeviceIoControl(
                _handle, Usn.FSCTL_QUERY_USN_JOURNAL,
                null, 0,
                output, (uint)sizeof(USN_JOURNAL_DATA_V0),
                out _, 0);
        }
    }

    private static SafeFileHandle OpenRoot(VolumeIdentity volume)
        => Kernel32.CreateFile(
            volume.RootPath,
            Kernel32.FILE_LIST_DIRECTORY | Kernel32.FILE_READ_ATTRIBUTES | Kernel32.SYNCHRONIZE,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE,
            0,
            Kernel32.OPEN_EXISTING,
            Kernel32.FILE_FLAG_BACKUP_SEMANTICS,
            0);

    private static UsnReadStatus Classify(int error)
        => error switch
        {
            Kernel32.ERROR_JOURNAL_NOT_ACTIVE => UsnReadStatus.JournalNotActive,
            Kernel32.ERROR_JOURNAL_DELETE_IN_PROGRESS or Kernel32.ERROR_JOURNAL_ENTRY_DELETED
                => UsnReadStatus.JournalInvalidated,
            Kernel32.ERROR_ACCESS_DENIED or Kernel32.ERROR_INVALID_FUNCTION => UsnReadStatus.AccessDenied,
            _ => UsnReadStatus.Failed,
        };

    private static DateTime ToDateTime(long fileTime)
    {
        const long MaxFileTime = 2650467743999999999;
        return fileTime <= 0 || fileTime > MaxFileTime ? DateTime.UnixEpoch : DateTime.FromFileTimeUtc(fileTime);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }
}
