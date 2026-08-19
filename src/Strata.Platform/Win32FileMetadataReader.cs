using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Platform.Native;

namespace Strata.Platform;

/// <summary>
/// Reads current file metadata by file ID.
/// </summary>
/// <remarks>
/// This is the other half of what makes Tier 2 cheap. The change journal says which files moved but
/// never how big they are, so the incremental scan reduces a tick's records to a distinct set of
/// file IDs and reads only those. A few thousand of these is milliseconds; the equivalent
/// information from a full walk costs a full walk.
/// <para>
/// Opening by ID rather than by path is deliberate: it needs no path reconstruction, it is immune to
/// a rename racing the read, and it works for files whose parent chain we have not resolved yet.
/// </para>
/// </remarks>
public sealed class Win32FileMetadataReader : IFileMetadataReader, IFilePathResolver, IDisposable
{
    private readonly SafeFileHandle _volumeHint;
    private bool _disposed;

    public Win32FileMetadataReader(VolumeIdentity volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        // Any handle on the volume serves as the hint that tells OpenFileById which volume the ID
        // belongs to. The root directory is the obvious choice and needs no elevation.
        _volumeHint = Kernel32.CreateFile(
            volume.RootPath,
            Kernel32.FILE_LIST_DIRECTORY | Kernel32.FILE_READ_ATTRIBUTES | Kernel32.SYNCHRONIZE,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE,
            0,
            Kernel32.OPEN_EXISTING,
            Kernel32.FILE_FLAG_BACKUP_SEMANTICS,
            0);
    }

    public bool IsOpen => !_volumeHint.IsInvalid;

    public bool TryReadById(long fileId, out FileMetadata metadata)
    {
        metadata = default;

        if (!IsOpen)
        {
            return false;
        }

        var descriptor = new FILE_ID_DESCRIPTOR
        {
            dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
            Type = Kernel32.FileIdType,
            FileId = fileId,
        };

        using var handle = Kernel32.OpenFileById(
            _volumeHint,
            ref descriptor,
            Kernel32.FILE_READ_ATTRIBUTES,

            // Share everything. A file being actively written by an installer is precisely the case
            // this exists to measure, and refusing to share would turn that into a read failure.
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE,
            0,
            Kernel32.FILE_FLAG_BACKUP_SEMANTICS);

        if (handle.IsInvalid)
        {
            // Routine: the file was deleted between the journal recording it and us looking. The
            // caller falls back to the size already recorded, which is the correct last-known value.
            return false;
        }

        if (!Kernel32.GetFileInformationByHandle(handle, out var info))
        {
            return false;
        }

        var logical = ((long)info.nFileSizeHigh << 32) | info.nFileSizeLow;

        metadata = new FileMetadata(
            logical,
            ReadAllocatedSize(handle, logical),
            info.dwFileAttributes,
            ToDateTime(((long)info.ftLastWriteTimeHigh << 32) | info.ftLastWriteTimeLow),

            // Link count comes free with this call, which is what lets Tier 2 know whether a size
            // change has to be fanned out across several directory entries.
            (int)info.nNumberOfLinks);

        return true;
    }

    /// <summary>
    /// Resolves a file's path, relative to the volume root, from its ID.
    /// </summary>
    /// <remarks>
    /// Needed because the unprivileged journal read redacts file names: every record comes back with
    /// FileNameLength of zero. Measured on the target machine, both V2 and V3 records, so it is a
    /// property of the unprivileged FSCTL rather than of a record version.
    /// <para>
    /// This recovers more than the name - FileNameInfo returns the whole path below the volume root,
    /// so it also resolves the parent chain for a file we have never seen. The cost is one extra
    /// call on a handle we are opening anyway to read the size. It only works for files that still
    /// exist, so deletions are resolved from our own recorded rows instead.
    /// </para>
    /// </remarks>
    public unsafe bool TryGetPath(long fileId, out string volumeRelativePath)
    {
        volumeRelativePath = "";

        if (!IsOpen)
        {
            return false;
        }

        var descriptor = new FILE_ID_DESCRIPTOR
        {
            dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
            Type = Kernel32.FileIdType,
            FileId = fileId,
        };

        using var handle = Kernel32.OpenFileById(
            _volumeHint,
            ref descriptor,
            Kernel32.FILE_READ_ATTRIBUTES,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE,
            0,
            Kernel32.FILE_FLAG_BACKUP_SEMANTICS);

        if (handle.IsInvalid)
        {
            return false;
        }

        // FILE_NAME_INFO is a length followed by an inline, non-terminated name. Long paths are
        // real on a machine with npm and NuGet caches, so the buffer is sized generously.
        const int bufferBytes = 4 + (32 * 1024 * 2);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferBytes);

        try
        {
            fixed (byte* raw = buffer)
            {
                if (!Kernel32.GetFileInformationByHandleEx(handle, Kernel32.FileNameInfo, raw, (uint)bufferBytes))
                {
                    return false;
                }

                var nameLength = (int)MemoryMarshal.Read<uint>(buffer.AsSpan(0, 4));

                if (nameLength <= 0 || 4 + nameLength > bufferBytes)
                {
                    return false;
                }

                volumeRelativePath = new string(MemoryMarshal.Cast<byte, char>(buffer.AsSpan(4, nameLength)));
                return true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads size on disk.
    /// </summary>
    /// <remarks>
    /// <c>BY_HANDLE_FILE_INFORMATION</c> carries only the logical size, unlike the directory records
    /// Tier 1 reads, so allocation size needs a second call. Falls back to the logical size rather
    /// than failing: an occasional imprecise size-on-disk is much better than dropping a change.
    /// </remarks>
    private static unsafe long ReadAllocatedSize(SafeFileHandle handle, long logicalSize)
    {
        FILE_STANDARD_INFO info;

        if (Kernel32.GetFileInformationByHandleEx(
                handle, Kernel32.FileStandardInfo, &info, (uint)sizeof(FILE_STANDARD_INFO)))
        {
            return info.AllocationSize;
        }

        return logicalSize;
    }

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
        _volumeHint.Dispose();
    }
}
