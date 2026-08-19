using System.Runtime.InteropServices;

namespace Strata.Platform.Native;

/// <summary>
/// Field offsets for FILE_ID_BOTH_DIR_INFO, as returned by GetFileInformationByHandleEx.
/// </summary>
/// <remarks>
/// The records arrive back to back in one buffer, each pointing at the next, with a variable-length
/// name inline. Rather than marshalling a struct per entry — millions of them over a full scan —
/// fields are read straight out of the buffer at fixed offsets.
/// <para>
/// The layout, with the C compiler's natural alignment:
/// </para>
/// <code>
///   0   ULONG         NextEntryOffset
///   4   ULONG         FileIndex
///   8   LARGE_INTEGER CreationTime
///  16   LARGE_INTEGER LastAccessTime
///  24   LARGE_INTEGER LastWriteTime
///  32   LARGE_INTEGER ChangeTime
///  40   LARGE_INTEGER EndOfFile        -- logical size
///  48   LARGE_INTEGER AllocationSize   -- size on disk
///  56   ULONG         FileAttributes
///  60   ULONG         FileNameLength   -- in bytes, not characters
///  64   ULONG         EaSize
///  68   CCHAR         ShortNameLength
///  70   WCHAR         ShortName[12]
///  96   LARGE_INTEGER FileId
/// 104   WCHAR         FileName[]
/// </code>
/// <para>
/// This is the one piece of the scanner where a wrong constant produces plausible-looking garbage
/// rather than an error, so <see cref="SelfTest"/> exists to check it against a directory whose
/// contents are known.
/// </para>
/// </remarks>
internal static class FileIdBothDirInfo
{
    internal const int OffsetNextEntryOffset = 0;
    internal const int OffsetLastWriteTime = 24;
    internal const int OffsetEndOfFile = 40;
    internal const int OffsetAllocationSize = 48;
    internal const int OffsetFileAttributes = 56;
    internal const int OffsetFileNameLength = 60;
    internal const int OffsetFileId = 96;
    internal const int OffsetFileName = 104;

    /// <summary>Smallest record that can be read without running off the end of the buffer.</summary>
    internal const int MinimumRecordSize = OffsetFileName;

    internal static uint NextEntryOffset(ReadOnlySpan<byte> record)
        => MemoryMarshal.Read<uint>(record[OffsetNextEntryOffset..]);

    internal static long LastWriteTime(ReadOnlySpan<byte> record)
        => MemoryMarshal.Read<long>(record[OffsetLastWriteTime..]);

    internal static long EndOfFile(ReadOnlySpan<byte> record)
        => MemoryMarshal.Read<long>(record[OffsetEndOfFile..]);

    internal static long AllocationSize(ReadOnlySpan<byte> record)
        => MemoryMarshal.Read<long>(record[OffsetAllocationSize..]);

    internal static uint FileAttributes(ReadOnlySpan<byte> record)
        => MemoryMarshal.Read<uint>(record[OffsetFileAttributes..]);

    internal static uint FileNameLength(ReadOnlySpan<byte> record)
        => MemoryMarshal.Read<uint>(record[OffsetFileNameLength..]);

    internal static long FileId(ReadOnlySpan<byte> record)
        => MemoryMarshal.Read<long>(record[OffsetFileId..]);

    /// <summary>The entry name, which is not null-terminated and is measured in bytes.</summary>
    internal static ReadOnlySpan<char> FileName(ReadOnlySpan<byte> record)
    {
        var lengthInBytes = (int)FileNameLength(record);
        var nameBytes = record.Slice(OffsetFileName, lengthInBytes);
        return MemoryMarshal.Cast<byte, char>(nameBytes);
    }
}
