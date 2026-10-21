using System.Buffers.Binary;
using Strata.Core.Model;
using Strata.Platform;

namespace Strata.Tests;

/// <summary>
/// Parsing a raw journal buffer, built byte for byte in the layout NTFS returns.
/// </summary>
public sealed class UsnRecordParsingTests
{
    private const uint USN_REASON_FILE_CREATE = 0x00000100;
    private const uint USN_REASON_FILE_DELETE = 0x00000200;
    private const uint USN_REASON_BASIC_INFO_CHANGE = 0x00008000;

    [Fact]
    public void AWholeBufferIsConsumedAndNamesNoResumePoint()
    {
        var buffer = Buffer(V2(100, USN_REASON_FILE_CREATE), V3(200, USN_REASON_FILE_DELETE));
        var into = new List<UsnChange>();

        var resume = Win32UsnJournal.ParseRecords(buffer, into, maxRecords: 10);

        Assert.Null(resume);
        Assert.Equal([100L, 200L], into.Select(c => c.Usn));
    }

    /// <summary>
    /// A tick that reaches its cap partway through a buffer has to resume at the first record it
    /// left. Resuming from the buffer header instead skipped the rest of the buffer for good, which
    /// on E: was 1,462 capped ticks each losing whatever followed the cap — deletions included.
    /// </summary>
    [Fact]
    public void ACapReachedMidBufferResumesAtTheFirstRecordNotTaken()
    {
        var buffer = Buffer(
            V2(100, USN_REASON_FILE_CREATE),
            V2(200, USN_REASON_FILE_CREATE),
            V3(300, USN_REASON_FILE_DELETE),
            V2(400, USN_REASON_FILE_DELETE));

        var into = new List<UsnChange>();

        var resume = Win32UsnJournal.ParseRecords(buffer, into, maxRecords: 2);

        Assert.Equal(300, resume);
        Assert.Equal(2, into.Count);
    }

    /// <summary>A record whose reasons cannot move a byte is consumed without counting toward the cap.</summary>
    [Fact]
    public void AFilteredRecordIsConsumedWithoutUsingTheCap()
    {
        var buffer = Buffer(
            V2(100, USN_REASON_BASIC_INFO_CHANGE),
            V2(200, USN_REASON_FILE_CREATE),
            V2(300, USN_REASON_FILE_CREATE));

        var into = new List<UsnChange>();

        var resume = Win32UsnJournal.ParseRecords(buffer, into, maxRecords: 1);

        Assert.Equal(300, resume);
        Assert.Equal(200, Assert.Single(into).Usn);
    }

    /// <summary>A cap reached on the buffer's last record leaves nothing behind to resume at.</summary>
    [Fact]
    public void ACapReachedOnTheLastRecordNamesNoResumePoint()
    {
        var buffer = Buffer(V2(100, USN_REASON_FILE_CREATE), V2(200, USN_REASON_FILE_CREATE));
        var into = new List<UsnChange>();

        Assert.Null(Win32UsnJournal.ParseRecords(buffer, into, maxRecords: 2));
        Assert.Equal(2, into.Count);
    }

    private static byte[] Buffer(params byte[][] records) => [.. records.SelectMany(r => r)];

    /// <summary>USN_RECORD_V2 with no name: 60 bytes of fields, padded to 64.</summary>
    private static byte[] V2(long usn, uint reason)
    {
        var record = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 64);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), usn + 1);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(16), 5);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(40), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), 0x80);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), 60);
        return record;
    }

    /// <summary>USN_RECORD_V3 with no name: 76 bytes of fields, padded to 80.</summary>
    private static byte[] V3(long usn, uint reason)
    {
        var record = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 80);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 3);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), usn + 1);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), 5);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(40), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(56), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(68), 0x80);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(74), 76);
        return record;
    }
}
