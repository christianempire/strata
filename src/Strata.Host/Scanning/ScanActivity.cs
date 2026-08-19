using Strata.Core.Scanning;

namespace Strata.Host.Scanning;

/// <summary>What the percentage is a percentage of.</summary>
/// <remarks>
/// Named rather than implied. A baseline's progress has two possible denominators and they are not
/// equally good, so the UI is told which one it got instead of presenting both as the same claim.
/// </remarks>
public enum ProgressBasis
{
    /// <summary>No denominator is known, so there is no percentage. Only the raw counts are real.</summary>
    None = 0,

    /// <summary>
    /// Entries seen against the last scan's entry count. The better basis, because enumeration cost
    /// tracks entries rather than bytes — and it needs a previous scan, so a genuine first run
    /// cannot have it.
    /// </summary>
    Entries = 1,

    /// <summary>
    /// Bytes seen against what Windows reports used. Available on a first run, and a rougher guide:
    /// a million small files and one large one weigh the same here and cost wildly different times.
    /// </summary>
    Bytes = 2,
}

/// <summary>
/// A scan that is happening right now, as a caller polling the API sees it.
/// </summary>
/// <remarks>
/// Everything on it is derived at read time from a handful of counters, rather than being a stored
/// projection updated as the scan runs. The counters are written from enumeration worker threads at
/// whatever rate the scan produces them; doing arithmetic there would put it on the hot path for the
/// benefit of a reader that arrives once a second at most.
/// </remarks>
public sealed record ScanActivitySnapshot
{
    public required string Volume { get; init; }

    public required ScanTier Tier { get; init; }

    public required string StartedUtc { get; init; }

    public long ElapsedMs { get; init; }

    public long DirectoriesCompleted { get; init; }

    public long FilesSeen { get; init; }

    public long BytesSeen { get; init; }

    /// <summary>The directory being read when the last progress sample was taken.</summary>
    public string CurrentPath { get; init; } = "";

    /// <summary>Depth of <see cref="CurrentPath"/>, so "reading deep" is visible as such.</summary>
    public int CurrentDepth { get; init; }

    public long EntriesPerSecond { get; init; }

    public ProgressBasis Basis { get; init; }

    /// <summary>Null when <see cref="Basis"/> is <see cref="ProgressBasis.None"/>.</summary>
    public double? Percent { get; init; }

    /// <summary>The denominator behind <see cref="Percent"/>, in the unit <see cref="Basis"/> names.</summary>
    public long? Expected { get; init; }

    /// <summary>
    /// Null until there is enough of the scan behind it to extrapolate from.
    /// </summary>
    /// <remarks>
    /// A time remaining that lurches is worse than no time remaining at all — it is the specific
    /// thing that makes a progress display read as broken. Below 2% the sample is too short to
    /// divide by, so nothing is claimed.
    /// </remarks>
    public long? RemainingMs { get; init; }
}

/// <summary>
/// The live counters behind one running scan.
/// </summary>
/// <remarks>
/// Written from the enumeration workers through <see cref="ScanOptions.OnProgress"/>, read from
/// whatever thread an HTTP request lands on. Interlocked rather than a lock, because the writer is
/// on the scan's hot path and the reader tolerates a sample that is a few directories stale — the
/// numbers describe a moving scan and were never going to be a consistent instant.
/// </remarks>
internal sealed class ScanActivity
{
    private readonly long _startedTicks = Environment.TickCount64;
    private long _directories;
    private long _files;
    private long _bytes;
    private string _currentPath = "";

    internal ScanActivity(string volume, ScanTier tier, long? expectedEntries, long expectedBytes)
    {
        Volume = volume;
        Tier = tier;
        StartedUtc = DateTime.UtcNow;
        ExpectedEntries = expectedEntries;
        ExpectedBytes = expectedBytes;
    }

    internal string Volume { get; }

    internal ScanTier Tier { get; }

    internal DateTime StartedUtc { get; }

    /// <summary>Entries the last scan of this volume found, or null if there has not been one.</summary>
    internal long? ExpectedEntries { get; }

    /// <summary>What Windows reports used on the volume. Always available, so a first run has a basis.</summary>
    internal long ExpectedBytes { get; }

    internal void Record(ScanProgress progress)
    {
        Interlocked.Exchange(ref _directories, progress.DirectoriesCompleted);
        Interlocked.Exchange(ref _files, progress.FilesSeen);
        Interlocked.Exchange(ref _bytes, progress.BytesSeen);

        // Reference assignment is already atomic; Volatile publishes it to the reading thread rather
        // than leaving it in a store buffer for an unbounded time.
        Volatile.Write(ref _currentPath, progress.CurrentPath);
    }

    internal ScanActivitySnapshot Snapshot()
    {
        var elapsedMs = Environment.TickCount64 - _startedTicks;
        var directories = Interlocked.Read(ref _directories);
        var files = Interlocked.Read(ref _files);
        var bytes = Interlocked.Read(ref _bytes);
        var path = Volatile.Read(ref _currentPath);

        var entries = directories + files;

        var (basis, fraction, expected) = Measure(entries, bytes);

        return new ScanActivitySnapshot
        {
            Volume = Volume,
            Tier = Tier,
            StartedUtc = StartedUtc.ToString("O"),
            ElapsedMs = elapsedMs,
            DirectoriesCompleted = directories,
            FilesSeen = files,
            BytesSeen = bytes,
            CurrentPath = path,
            CurrentDepth = Depth(path),
            EntriesPerSecond = elapsedMs > 0 ? entries * 1000 / elapsedMs : 0,
            Basis = basis,
            Percent = fraction is null ? null : Math.Round(fraction.Value * 100, 1),
            Expected = expected,
            RemainingMs = Remaining(elapsedMs, fraction),
        };
    }

    /// <summary>
    /// Picks the best denominator available and reports which one it was.
    /// </summary>
    /// <remarks>
    /// Entries first because that is what enumeration cost tracks. Bytes as the fallback rather than
    /// no percentage at all, because the state this exists to serve is the first run — where there
    /// is no previous scan to count entries against, and where a bar is most needed.
    /// </remarks>
    private (ProgressBasis Basis, double? Fraction, long? Expected) Measure(long entries, long bytes)
    {
        if (ExpectedEntries is > 0)
        {
            return (ProgressBasis.Entries,
                Math.Min(1.0, (double)entries / ExpectedEntries.Value),
                ExpectedEntries);
        }

        if (ExpectedBytes > 0)
        {
            return (ProgressBasis.Bytes, Math.Min(1.0, (double)bytes / ExpectedBytes), ExpectedBytes);
        }

        return (ProgressBasis.None, null, null);
    }

    /// <summary>
    /// Extrapolates linearly from what is done, or declines to.
    /// </summary>
    /// <remarks>
    /// Linear is wrong in detail — the deepest branches come last and read slower — but it is
    /// right in the only way that matters here, which is that the figure falls monotonically rather
    /// than jumping about. Nothing is claimed until 2% is done, because dividing by a fraction that
    /// small produces an estimate in hours that then collapses, and a first impression of "this will
    /// take four hours" is not recoverable by being right thirty seconds later.
    /// </remarks>
    private static long? Remaining(long elapsedMs, double? fraction)
        => fraction is >= 0.02 and < 1.0 && elapsedMs > 0
            ? (long)(elapsedMs / fraction.Value) - elapsedMs
            : null;

    /// <summary>How deep the reader currently is, counted from the path rather than reported.</summary>
    /// <remarks>
    /// Derived here rather than added to <see cref="ScanProgress"/>: the scan already builds this
    /// string, and counting separators once per progress sample is free where anything per-entry is
    /// not.
    /// </remarks>
    private static int Depth(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }

        // Scan paths are the extended-length form the enumerator walks — \\?\C:\Users, or a
        // \\?\Volume{...}\ root — and its four leading characters would otherwise count as levels.
        var span = path.AsSpan();

        if (span.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            span = span[4..];
        }

        var depth = 0;

        foreach (var c in span.TrimEnd('\\'))
        {
            if (c == '\\')
            {
                depth++;
            }
        }

        // A volume root is "C:\", which trims to "C:" and counts zero separators. Anything below it
        // counts one separator per level, so the count is already the depth.
        return depth;
    }
}
