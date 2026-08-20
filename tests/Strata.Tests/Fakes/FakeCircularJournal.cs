using Strata.Core.Model;
using Strata.Core.Scanning;

namespace Strata.Tests.Fakes;

/// <summary>
/// A change journal that is finite, and trims the oldest records once it is full.
/// </summary>
/// <remarks>
/// <see cref="FakeUsnJournal"/> models a journal as an unbounded list, which is what the code
/// believed a journal was: a durable queue you can always resume from. It is not. On the target
/// machine C:'s journal is a 32 MiB circular buffer, and a scanner that falls further behind than
/// that has nothing left to resume from — which is precisely how C: went 27 hours and E: went 25
/// without a tick, with no error anywhere except a deferral message repeating itself.
/// <para>
/// This fake exists so that failure can be provoked in under a second rather than waited for in a
/// day. It advances a USN per record, drops records off the front past <see cref="Capacity"/>, and
/// reports <see cref="UsnReadStatus.JournalInvalidated"/> to a reader whose cursor has fallen behind
/// the front — the same three behaviours the real thing has, and the only three that matter here.
/// </para>
/// </remarks>
internal sealed class FakeCircularJournal : IUsnJournal
{
    /// <summary>
    /// USN bytes one record occupies.
    /// </summary>
    /// <remarks>
    /// A USN is a byte offset into the journal, not a record ordinal, which is why the cursor in the
    /// incident was "472 million behind" rather than "4.7 million records behind". A round hundred
    /// is close enough to the real average and keeps the arithmetic in a failing test readable.
    /// </remarks>
    internal const long BytesPerRecord = 100;

    /// <summary>
    /// Distinct files the churn touches, cycled through.
    /// </summary>
    /// <remarks>
    /// A volume being used rewrites the same files over and over — a build directory, a log, a
    /// package cache — which is why coalescing a tick down to the distinct set is worth doing at
    /// all. Giving every record its own file id would model a machine that only ever creates, and
    /// would leave the recorded state growing without bound in a test about pacing.
    /// </remarks>
    private const int DistinctFiles = 256;

    private readonly Queue<Record> _window = new();

    private long _nextUsn;
    private long _written;

    /// <summary>The furthest a reader has consumed to. Everything below it has been seen.</summary>
    private long _delivered;

    /// <summary>Records the journal holds before the oldest are trimmed. The buffer's whole size.</summary>
    internal required int Capacity { get; init; }

    internal ulong JournalId { get; set; } = 0x1DB931EBE461434;

    /// <summary>The oldest USN still readable. Rises as the buffer wraps.</summary>
    internal long FirstUsn => _window.Count == 0 ? _nextUsn : _window.Peek().Usn;

    /// <summary>Records evicted from the buffer, read or not. A fact about the buffer, not a fault.</summary>
    internal int Trimmed { get; private set; }

    /// <summary>
    /// Records that fell out of the buffer before anyone read them.
    /// </summary>
    /// <remarks>
    /// The number the incident is about. A journal evicting records a reader has already consumed is
    /// a circular buffer working; evicting ones it has not is history destroyed, and there is no
    /// later moment at which it can be recovered.
    /// </remarks>
    internal int Lost { get; private set; }

    /// <summary>How many times a reader was told its cursor no longer exists.</summary>
    internal int Invalidations { get; private set; }

    /// <summary>Writes <paramref name="count"/> records, as a volume being used does.</summary>
    internal void Churn(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _window.Enqueue(new Record(_nextUsn, 1000 + _written++ % DistinctFiles));
            _nextUsn += BytesPerRecord;

            if (_window.Count > Capacity)
            {
                var evicted = _window.Dequeue();

                Trimmed++;

                if (evicted.Usn >= _delivered)
                {
                    Lost++;
                }
            }
        }
    }

    public UsnJournalState? TryGetState() => new(JournalId, FirstUsn, _nextUsn);

    public UsnReadResult Read(UsnCursor cursor, int maxRecords)
    {
        if (cursor.IsSet && !new UsnJournalState(JournalId, FirstUsn, _nextUsn).CanResumeFrom(cursor))
        {
            Invalidations++;
            return new UsnReadResult { Status = UsnReadStatus.JournalInvalidated };
        }

        // An unset cursor starts at the head, exactly as Win32UsnJournal does: a baseline has just
        // read the whole volume, so everything before this instant is already accounted for.
        var start = cursor.IsSet ? cursor.NextUsn : _nextUsn;

        var changes = new List<UsnChange>();
        var next = start;
        var remaining = 0;

        foreach (var record in _window)
        {
            if (record.Usn < start)
            {
                continue;
            }

            if (changes.Count >= maxRecords)
            {
                remaining++;
                continue;
            }

            changes.Add(new UsnChange(
                Usn: record.Usn,
                FileId: record.FileId,
                ParentId: 5,
                Name: $"f{record.FileId}",
                Kind: UsnChangeKind.DataChanged,
                RawAttributes: 0x80,
                TimestampUtc: DateTime.UnixEpoch));

            next = record.Usn + BytesPerRecord;
        }

        _delivered = next > _delivered ? next : _delivered;

        return new UsnReadResult
        {
            Status = UsnReadStatus.Ok,
            Changes = changes,
            NextUsn = next,
            JournalId = JournalId,
            MoreAvailable = remaining > 0,
        };
    }

    private readonly record struct Record(long Usn, long FileId);
}
