using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Strata.Platform.Native;

/// <summary>
/// Raw kernel32 entry points.
/// </summary>
/// <remarks>
/// Native names and casing are kept verbatim so every declaration can be grepped straight against
/// MSDN. Managed-looking wrappers live outside this namespace.
/// </remarks>
internal static partial class Kernel32
{
    internal const string Library = "kernel32.dll";

    // --- Access rights and flags -------------------------------------------------------------

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint FILE_LIST_DIRECTORY = 0x0001;
    internal const uint FILE_READ_ATTRIBUTES = 0x0080;
    internal const uint SYNCHRONIZE = 0x00100000;

    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint FILE_SHARE_DELETE = 0x00000004;

    internal const uint OPEN_EXISTING = 3;

    /// <summary>Required to obtain a handle to a directory rather than to a file.</summary>
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    // --- Errors ------------------------------------------------------------------------------

    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_PATH_NOT_FOUND = 3;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_NO_MORE_FILES = 18;
    internal const int ERROR_NOT_READY = 21;
    internal const int ERROR_SHARING_VIOLATION = 32;
    internal const int ERROR_HANDLE_EOF = 38;
    internal const int ERROR_INVALID_PARAMETER = 87;
    internal const int ERROR_INVALID_NAME = 123;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_NO_MORE_ITEMS = 259;
    internal const int ERROR_DIRECTORY = 267;
    internal const int ERROR_FILENAME_EXCED_RANGE = 206;
    internal const int ERROR_INVALID_FUNCTION = 1;

    /// <summary>The volume has no active change journal. Creating one needs elevation.</summary>
    internal const int ERROR_JOURNAL_NOT_ACTIVE = 1179;

    /// <summary>The journal was deleted and recreated; any stored cursor is meaningless.</summary>
    internal const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;

    /// <summary>The requested USN has already been trimmed from the journal.</summary>
    internal const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    // --- Drive types, as returned by GetDriveTypeW --------------------------------------------

    internal const uint DRIVE_UNKNOWN = 0;
    internal const uint DRIVE_NO_ROOT_DIR = 1;
    internal const uint DRIVE_REMOVABLE = 2;
    internal const uint DRIVE_FIXED = 3;
    internal const uint DRIVE_REMOTE = 4;
    internal const uint DRIVE_CDROM = 5;
    internal const uint DRIVE_RAMDISK = 6;

    // --- Thread priority ----------------------------------------------------------------------

    /// <summary>
    /// Lowers the calling thread's CPU, memory and — the part that matters here — I/O priority.
    /// Per-thread, unlike the PROCESS_MODE equivalent, so a scan cannot throttle the API threads
    /// sharing this process.
    /// </summary>
    internal const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;

    internal const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    // --- File information classes, from FILE_INFO_BY_HANDLE_CLASS -----------------------------

    internal const int FileStandardInfo = 1;
    internal const int FileNameInfo = 2;
    internal const int FileIdBothDirectoryInfo = 10;
    internal const int FileIdBothDirectoryRestartInfo = 11;
    internal const int FileIoPriorityHintInfo = 12;

    /// <summary>Values for FILE_IO_PRIORITY_HINT_INFO.</summary>
    internal const int IoPriorityHintVeryLow = 0;

    /// <summary>FILE_ID_TYPE.FileIdType: the 64-bit NTFS file reference number.</summary>
    internal const uint FileIdType = 0;

    // --- Device control -----------------------------------------------------------------------

    internal const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;

    [LibraryImport(Library, EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int FileInformationClass,
        void* lpFileInformation,
        uint dwBufferSize);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int FileInformationClass,
        void* lpFileInformation,
        uint dwBufferSize);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial SafeFileHandle OpenFileById(
        SafeFileHandle hVolumeHint,
        ref FILE_ID_DESCRIPTOR lpFileId,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwFlagsAndAttributes);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        void* lpInBuffer,
        uint nInBufferSize,
        void* lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    // --- Volume enumeration ---------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "FindFirstVolumeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFindVolumeHandle FindFirstVolume(
        [Out] char[] lpszVolumeName,
        uint cchBufferLength);

    [LibraryImport(Library, EntryPoint = "FindNextVolumeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindNextVolume(
        SafeFindVolumeHandle hFindVolume,
        [Out] char[] lpszVolumeName,
        uint cchBufferLength);

    [LibraryImport(Library, EntryPoint = "FindVolumeClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindVolumeClose(nint hFindVolume);

    [LibraryImport(Library, EntryPoint = "GetVolumePathNamesForVolumeNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumePathNamesForVolumeName(
        string lpszVolumeName,
        [Out] char[] lpszVolumePathNames,
        uint cchBufferLength,
        out uint lpcchReturnLength);

    [LibraryImport(Library, EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetDriveType(string lpRootPathName);

    [LibraryImport(Library, EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformation(
        string lpRootPathName,
        [Out] char[] lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        [Out] char[] lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [LibraryImport(Library, EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    // --- Threads -------------------------------------------------------------------------------

    [LibraryImport(Library)]
    internal static partial nint GetCurrentThread();

    /// <summary>
    /// System-wide idle, kernel and user time. Kernel time already includes idle time.
    /// </summary>
    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(
        out FILETIME lpIdleTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadPriority(nint hThread, int nPriority);
}

/// <remarks>
/// The timestamps are FILETIME, which is a pair of DWORDs and therefore 4-byte aligned. Declaring
/// them as <see langword="long"/> would give them 8-byte alignment and shift every field after the
/// first one, so they are kept as DWORD pairs to match the C layout exactly.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct BY_HANDLE_FILE_INFORMATION
{
    internal uint dwFileAttributes;
    internal uint ftCreationTimeLow;
    internal uint ftCreationTimeHigh;
    internal uint ftLastAccessTimeLow;
    internal uint ftLastAccessTimeHigh;
    internal uint ftLastWriteTimeLow;
    internal uint ftLastWriteTimeHigh;
    internal uint dwVolumeSerialNumber;
    internal uint nFileSizeHigh;
    internal uint nFileSizeLow;
    internal uint nNumberOfLinks;
    internal uint nFileIndexHigh;
    internal uint nFileIndexLow;

    /// <summary>The 64-bit NTFS file reference number, which is what the whole storage model keys on.</summary>
    internal readonly long FileId => ((long)nFileIndexHigh << 32) | nFileIndexLow;
}

/// <summary>
/// FILE_ID_DESCRIPTOR, as taken by OpenFileById.
/// </summary>
/// <remarks>
/// The real structure ends in a union of a LARGE_INTEGER, a GUID and a FILE_ID_128, so it is 16
/// bytes wide from offset 8. Only the 64-bit form is used here, but the padding has to be present
/// or dwSize will not match what the API expects.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ID_DESCRIPTOR
{
    internal uint dwSize;
    internal uint Type;
    internal long FileId;
    private readonly long _unionPadding;
}

/// <summary>FILE_STANDARD_INFO. AllocationSize is the size on disk.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FILE_STANDARD_INFO
{
    internal long AllocationSize;
    internal long EndOfFile;
    internal uint NumberOfLinks;
    [MarshalAs(UnmanagedType.U1)] internal bool DeletePending;
    [MarshalAs(UnmanagedType.U1)] internal bool Directory;
}

/// <summary>FILETIME as a pair of DWORDs, matching the C layout's 4-byte alignment.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FILETIME
{
    internal uint dwLowDateTime;
    internal uint dwHighDateTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STORAGE_DEVICE_NUMBER
{
    internal uint DeviceType;
    internal uint DeviceNumber;
    internal uint PartitionNumber;
}

/// <summary>Handle returned by FindFirstVolumeW, closed with FindVolumeClose rather than CloseHandle.</summary>
internal sealed class SafeFindVolumeHandle() : SafeHandle(nint.Zero, ownsHandle: true)
{
    public override bool IsInvalid => handle == nint.Zero || handle == -1;

    protected override bool ReleaseHandle() => Kernel32.FindVolumeClose(handle);
}
