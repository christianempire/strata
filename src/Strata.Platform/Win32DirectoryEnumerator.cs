using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Platform.Native;

namespace Strata.Platform;

/// <summary>
/// Reads directories through GetFileInformationByHandleEx with FILE_ID_BOTH_DIR_INFO.
/// </summary>
/// <remarks>
/// This information class is the reason a full scan is affordable. One directory read yields, per
/// entry and with no further syscall: the file ID, the logical size, the size on disk, the
/// attributes, and the timestamps. Anything that has to open or stat each file individually to get
/// those fields does millions of extra syscalls to learn the same thing.
/// <para>
/// GetFileInformationByHandleEx rather than NtQueryDirectoryFile: it issues the same call with the
/// same information class and returns the same records, but it is documented, supported Win32
/// rather than an ntdll contract. See docs/DESIGN-REVIEW.md section A2.
/// </para>
/// <para>Safe to call concurrently; it holds no per-call state beyond what the caller passes in.</para>
/// </remarks>
public sealed class Win32DirectoryEnumerator : IDirectoryEnumerator
{
    /// <summary>
    /// Big enough that a typical directory comes back in one call, small enough to stay off the
    /// large object heap and inside cache. Buffers are pooled per call, not per entry.
    /// </summary>
    private const int BufferSize = 64 * 1024;

    private readonly bool _setPerHandleIoPriority;

    /// <summary>
    /// Tracks whether the per-handle I/O priority hint is actually being accepted, so the CLI can
    /// report what resource discipline is genuinely in effect rather than what was requested.
    /// </summary>
    private int _ioPriorityHintFailures;

    public Win32DirectoryEnumerator(bool setPerHandleIoPriority = true)
        => _setPerHandleIoPriority = setPerHandleIoPriority;

    public int IoPriorityHintFailures => Volatile.Read(ref _ioPriorityHintFailures);

    public unsafe bool TryEnumerate(
        string fullPath,
        List<DirectoryEntry> buffer,
        out DirectoryEnumerationError failure,
        out int nativeErrorCode)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        buffer.Clear();
        nativeErrorCode = 0;

        using var handle = OpenDirectory(fullPath);

        if (handle.IsInvalid)
        {
            nativeErrorCode = Marshal.GetLastPInvokeError();
            failure = Classify(nativeErrorCode);
            return false;
        }

        if (_setPerHandleIoPriority)
        {
            ApplyIoPriorityHint(handle);
        }

        var rented = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            var infoClass = Kernel32.FileIdBothDirectoryRestartInfo;

            fixed (byte* raw = rented)
            {
                while (true)
                {
                    if (!Kernel32.GetFileInformationByHandleEx(handle, infoClass, raw, (uint)rented.Length))
                    {
                        var error = Marshal.GetLastPInvokeError();

                        // The documented way an enumeration ends.
                        if (error is Kernel32.ERROR_NO_MORE_FILES or Kernel32.ERROR_HANDLE_EOF)
                        {
                            failure = DirectoryEnumerationError.None;
                            return true;
                        }

                        nativeErrorCode = error;
                        failure = Classify(error);
                        buffer.Clear();
                        return false;
                    }

                    // Subsequent calls continue rather than restart.
                    infoClass = Kernel32.FileIdBothDirectoryInfo;

                    ReadRecords(new ReadOnlySpan<byte>(raw, rented.Length), buffer);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Walks the chain of variable-length records packed into one buffer.</summary>
    private static void ReadRecords(ReadOnlySpan<byte> buffer, List<DirectoryEntry> into)
    {
        var offset = 0;

        while (true)
        {
            if (offset + FileIdBothDirInfo.MinimumRecordSize > buffer.Length)
            {
                return;
            }

            var record = buffer[offset..];
            var nameLength = (int)FileIdBothDirInfo.FileNameLength(record);

            if (FileIdBothDirInfo.OffsetFileName + nameLength > record.Length)
            {
                return;
            }

            var name = FileIdBothDirInfo.FileName(record);

            // "." and ".." come back from every directory and are never entries in their own right.
            if (!IsDotEntry(name))
            {
                var attributes = FileIdBothDirInfo.FileAttributes(record);

                into.Add(new DirectoryEntry(
                    FileIdBothDirInfo.FileId(record),
                    new string(name),
                    FileIdBothDirInfo.EndOfFile(record),
                    FileIdBothDirInfo.AllocationSize(record),
                    ToTraits(attributes),
                    attributes,
                    ToDateTime(FileIdBothDirInfo.LastWriteTime(record))));
            }

            var next = FileIdBothDirInfo.NextEntryOffset(record);

            if (next == 0)
            {
                return;
            }

            offset += (int)next;
        }
    }

    private static bool IsDotEntry(ReadOnlySpan<char> name)
        => name.Length switch
        {
            1 => name[0] == '.',
            2 => name[0] == '.' && name[1] == '.',
            _ => false,
        };

    public IDisposable EnterBackgroundIoScope() => new BackgroundIoScope();

    public bool TryGetDirectoryId(string fullPath, out long fileId)
    {
        fileId = 0;

        using var handle = OpenDirectory(fullPath);

        if (handle.IsInvalid)
        {
            return false;
        }

        if (!Kernel32.GetFileInformationByHandle(handle, out var info))
        {
            return false;
        }

        fileId = info.FileId;
        return true;
    }

    private static SafeFileHandle OpenDirectory(string fullPath)
        => Kernel32.CreateFile(
            fullPath,
            Kernel32.FILE_LIST_DIRECTORY | Kernel32.FILE_READ_ATTRIBUTES | Kernel32.SYNCHRONIZE,

            // Share everything. A scan must never be the reason another process cannot open,
            // write, or delete a file, and a sharing violation would show up as a phantom
            // inaccessible directory.
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE,
            lpSecurityAttributes: 0,
            Kernel32.OPEN_EXISTING,
            Kernel32.FILE_FLAG_BACKUP_SEMANTICS,
            hTemplateFile: 0);

    /// <summary>
    /// Asks for very low I/O priority on this handle specifically.
    /// </summary>
    /// <remarks>
    /// Complements the per-thread background mode rather than replacing it, and is best-effort:
    /// if the filesystem declines the hint the scan is still running on a background-priority
    /// thread. Failures are counted so the CLI can report what is actually in effect.
    /// </remarks>
    private unsafe void ApplyIoPriorityHint(SafeFileHandle handle)
    {
        var hint = Kernel32.IoPriorityHintVeryLow;

        if (!Kernel32.SetFileInformationByHandle(
                handle,
                Kernel32.FileIoPriorityHintInfo,
                &hint,
                sizeof(int)))
        {
            Interlocked.Increment(ref _ioPriorityHintFailures);
        }
    }

    private static EntryTraits ToTraits(uint attributes)
    {
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
        const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
        const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;
        const uint FILE_ATTRIBUTE_COMPRESSED = 0x00000800;
        const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x00000200;
        const uint FILE_ATTRIBUTE_OFFLINE = 0x00001000;
        const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

        var traits = EntryTraits.None;

        if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            traits |= EntryTraits.Directory;
        }

        if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            traits |= EntryTraits.ReparsePoint;
        }

        if ((attributes & FILE_ATTRIBUTE_HIDDEN) != 0)
        {
            traits |= EntryTraits.Hidden;
        }

        if ((attributes & FILE_ATTRIBUTE_SYSTEM) != 0)
        {
            traits |= EntryTraits.System;
        }

        if ((attributes & FILE_ATTRIBUTE_COMPRESSED) != 0)
        {
            traits |= EntryTraits.Compressed;
        }

        if ((attributes & FILE_ATTRIBUTE_SPARSE_FILE) != 0)
        {
            traits |= EntryTraits.Sparse;
        }

        if ((attributes & (FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0)
        {
            traits |= EntryTraits.Offline;
        }

        return traits;
    }

    /// <summary>
    /// Converts a FILETIME, tolerating the nonsense values that show up on real volumes.
    /// </summary>
    /// <remarks>
    /// A handful of files on any Windows install carry zero or wildly out-of-range timestamps, and
    /// DateTime.FromFileTimeUtc throws on those. Failing a whole scan over a bad mtime would be
    /// absurd, so they clamp.
    /// </remarks>
    private static DateTime ToDateTime(long fileTime)
    {
        const long MaxFileTime = 2650467743999999999; // 31 Dec 9999 in FILETIME ticks

        if (fileTime <= 0 || fileTime > MaxFileTime)
        {
            return DateTime.UnixEpoch;
        }

        return DateTime.FromFileTimeUtc(fileTime);
    }

    private static DirectoryEnumerationError Classify(int nativeErrorCode)
        => nativeErrorCode switch
        {
            Kernel32.ERROR_ACCESS_DENIED => DirectoryEnumerationError.AccessDenied,
            Kernel32.ERROR_FILE_NOT_FOUND or Kernel32.ERROR_PATH_NOT_FOUND
                => DirectoryEnumerationError.NotFound,
            Kernel32.ERROR_INVALID_NAME or Kernel32.ERROR_FILENAME_EXCED_RANGE
                => DirectoryEnumerationError.PathTooLong,
            _ => DirectoryEnumerationError.Other,
        };
}
