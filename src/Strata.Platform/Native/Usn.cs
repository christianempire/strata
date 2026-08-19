using System.Runtime.InteropServices;

namespace Strata.Platform.Native;

/// <summary>
/// NTFS change journal control codes and record layouts.
/// </summary>
/// <remarks>
/// The FSCTL values are computed by the CTL_CODE macro as
/// <c>(DeviceType &lt;&lt; 16) | (Access &lt;&lt; 14) | (Function &lt;&lt; 2) | Method</c>, with
/// DeviceType = FILE_DEVICE_FILE_SYSTEM (9) and FILE_ANY_ACCESS (0) throughout. The arithmetic is
/// written out beside each one so they can be checked rather than trusted.
/// </remarks>
internal static class Usn
{
    /// <summary>Function 61, METHOD_BUFFERED. Returns the journal ID and its USN bounds.</summary>
    internal const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;

    /// <summary>Function 46, METHOD_NEITHER. Requires an elevated volume handle.</summary>
    internal const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;

    /// <summary>
    /// Function 234, METHOD_NEITHER. Added in Windows 8 so non-elevated callers can read the
    /// journal, filtered to records for files the caller can already see.
    /// </summary>
    internal const uint FSCTL_READ_UNPRIVILEGED_USN_JOURNAL = 0x000903AB;

    /// <summary>Function 57, METHOD_NEITHER. Creates or resizes the journal. Always needs elevation.</summary>
    internal const uint FSCTL_CREATE_USN_JOURNAL = 0x000900E7;

    // --- Reason flags -------------------------------------------------------------------------

    internal const uint USN_REASON_DATA_OVERWRITE = 0x00000001;
    internal const uint USN_REASON_DATA_EXTEND = 0x00000002;
    internal const uint USN_REASON_DATA_TRUNCATION = 0x00000004;
    internal const uint USN_REASON_NAMED_DATA_OVERWRITE = 0x00000010;
    internal const uint USN_REASON_NAMED_DATA_EXTEND = 0x00000020;
    internal const uint USN_REASON_NAMED_DATA_TRUNCATION = 0x00000040;
    internal const uint USN_REASON_FILE_CREATE = 0x00000100;
    internal const uint USN_REASON_FILE_DELETE = 0x00000200;
    internal const uint USN_REASON_EA_CHANGE = 0x00000400;
    internal const uint USN_REASON_SECURITY_CHANGE = 0x00000800;
    internal const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    internal const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
    internal const uint USN_REASON_INDEXABLE_CHANGE = 0x00004000;
    internal const uint USN_REASON_BASIC_INFO_CHANGE = 0x00008000;
    internal const uint USN_REASON_HARD_LINK_CHANGE = 0x00010000;
    internal const uint USN_REASON_COMPRESSION_CHANGE = 0x00020000;
    internal const uint USN_REASON_ENCRYPTION_CHANGE = 0x00040000;
    internal const uint USN_REASON_OBJECT_ID_CHANGE = 0x00080000;
    internal const uint USN_REASON_REPARSE_POINT_CHANGE = 0x00100000;
    internal const uint USN_REASON_STREAM_CHANGE = 0x00200000;
    internal const uint USN_REASON_TRANSACTED_CHANGE = 0x00400000;
    internal const uint USN_REASON_CLOSE = 0x80000000;

    /// <summary>
    /// The reasons that can change what Strata reports, used as the read mask.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>0xFFFFFFFF</c>. Security and EA changes, object-id churn and basic-info
    /// touches are extremely common and can never move a byte, so filtering them at the kernel
    /// boundary is free and keeps the per-tick record count down. Reparse and hardlink changes are
    /// included because both alter how bytes are attributed even when no data is written.
    /// </remarks>
    internal const uint InterestingReasons =
        USN_REASON_DATA_OVERWRITE | USN_REASON_DATA_EXTEND | USN_REASON_DATA_TRUNCATION |
        USN_REASON_NAMED_DATA_OVERWRITE | USN_REASON_NAMED_DATA_EXTEND | USN_REASON_NAMED_DATA_TRUNCATION |
        USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE |
        USN_REASON_RENAME_OLD_NAME | USN_REASON_RENAME_NEW_NAME |
        USN_REASON_HARD_LINK_CHANGE | USN_REASON_REPARSE_POINT_CHANGE |
        USN_REASON_COMPRESSION_CHANGE | USN_REASON_STREAM_CHANGE;

    // --- USN_RECORD_V2 field offsets -----------------------------------------------------------
    //
    //   0  DWORD     RecordLength
    //   4  WORD      MajorVersion
    //   6  WORD      MinorVersion
    //   8  DWORDLONG FileReferenceNumber
    //  16  DWORDLONG ParentFileReferenceNumber
    //  24  USN       Usn                       (LONGLONG)
    //  32  LARGE_INTEGER TimeStamp
    //  40  DWORD     Reason
    //  44  DWORD     SourceInfo
    //  48  DWORD     SecurityId
    //  52  DWORD     FileAttributes
    //  56  WORD      FileNameLength            (bytes)
    //  58  WORD      FileNameOffset            (bytes, from record start)
    //  60  WCHAR     FileName[]

    internal const int V2FileReferenceNumber = 8;
    internal const int V2ParentFileReferenceNumber = 16;
    internal const int V2Usn = 24;
    internal const int V2TimeStamp = 32;
    internal const int V2Reason = 40;
    internal const int V2FileAttributes = 52;
    internal const int V2FileNameLength = 56;
    internal const int V2FileNameOffset = 58;

    // --- USN_RECORD_V3 field offsets -----------------------------------------------------------
    //
    // Identical in spirit, but the file references are FILE_ID_128 rather than DWORDLONG, which
    // shifts everything after them by 16 bytes.
    //
    //   8  FILE_ID_128 FileReferenceNumber       (16 bytes)
    //  24  FILE_ID_128 ParentFileReferenceNumber (16 bytes)
    //  40  USN       Usn
    //  48  LARGE_INTEGER TimeStamp
    //  56  DWORD     Reason
    //  60  DWORD     SourceInfo
    //  64  DWORD     SecurityId
    //  68  DWORD     FileAttributes
    //  72  WORD      FileNameLength
    //  74  WORD      FileNameOffset
    //  76  WCHAR     FileName[]

    internal const int V3FileReferenceNumber = 8;
    internal const int V3ParentFileReferenceNumber = 24;
    internal const int V3Usn = 40;
    internal const int V3TimeStamp = 48;
    internal const int V3Reason = 56;
    internal const int V3FileAttributes = 68;
    internal const int V3FileNameLength = 72;
    internal const int V3FileNameOffset = 74;

    /// <summary>Common header offsets, identical across versions.</summary>
    internal const int RecordLength = 0;

    internal const int MajorVersion = 4;
}

/// <summary>USN_JOURNAL_DATA_V0. Later versions append fields, so this prefix stays valid.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct USN_JOURNAL_DATA_V0
{
    internal ulong UsnJournalID;
    internal long FirstUsn;
    internal long NextUsn;
    internal long LowestValidUsn;
    internal long MaxUsn;
    internal ulong MaximumSize;
    internal ulong AllocationDelta;
}

/// <summary>
/// READ_USN_JOURNAL_DATA_V1.
/// </summary>
/// <remarks>
/// V1 rather than V0 because the version range fields are what allow both USN_RECORD_V2 and V3 to be
/// accepted. ReFS only emits V3; NTFS emits V2 unless a wider range is requested.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct READ_USN_JOURNAL_DATA_V1
{
    internal long StartUsn;
    internal uint ReasonMask;
    internal uint ReturnOnlyOnClose;
    internal ulong Timeout;
    internal ulong BytesToWaitFor;
    internal ulong UsnJournalID;
    internal ushort MinMajorVersion;
    internal ushort MaxMajorVersion;
}

/// <summary>CREATE_USN_JOURNAL_DATA.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CREATE_USN_JOURNAL_DATA
{
    internal ulong MaximumSize;
    internal ulong AllocationDelta;
}
