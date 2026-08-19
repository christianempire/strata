namespace Strata.Core.Rollup;

/// <summary>How many directories a given (depth cap, size floor) pair would put in a snapshot.</summary>
public readonly record struct HistogramCell(int DepthCap, long SizeFloorBytes, int DirectoryCount)
{
    /// <summary>
    /// Rough bytes this cell would cost per snapshot, dominated by the denormalised path string.
    /// Deliberately an estimate: the point is the order of magnitude, not the exact figure.
    /// </summary>
    public long EstimatedBytesPerSnapshot => DirectoryCount * EstimatedBytesPerRow;

    /// <summary>
    /// Row overhead plus a typical path. Measured against the real schema once snapshots exist;
    /// until then this is a stated assumption rather than a measurement.
    /// </summary>
    public const int EstimatedBytesPerRow = 90;
}

/// <summary>
/// Counts how many directories would qualify for <c>snapshot_dirs</c> across a grid of depth caps
/// and size floors.
/// </summary>
/// <remarks>
/// This exists to answer an open design question rather than to serve a product feature.
/// <para>
/// The historical rollup is written once per snapshot, and snapshots are taken every 15 to 30
/// minutes with seven days kept at full resolution — roughly 672 snapshots. Whether that is a
/// 250 MB database or a 6 GB one turns entirely on how many directories clear the size floor on a
/// real volume, and that number is not something worth estimating when it can be measured.
/// See docs/DESIGN-REVIEW.md section A5.
/// </para>
/// </remarks>
public static class DirectoryHistogram
{
    public static readonly long[] DefaultFloors =
    [
        1L * 1024 * 1024,
        10L * 1024 * 1024,
        100L * 1024 * 1024,
        1024L * 1024 * 1024,
    ];

    public static readonly int[] DefaultDepthCaps = [4, 6, 8, 10, int.MaxValue];

    public static IReadOnlyList<HistogramCell> Compute(
        RollupResult rollup,
        IReadOnlyList<int>? depthCaps = null,
        IReadOnlyList<long>? floors = null)
    {
        ArgumentNullException.ThrowIfNull(rollup);

        depthCaps ??= DefaultDepthCaps;
        floors ??= DefaultFloors;

        var cells = new List<HistogramCell>(depthCaps.Count * floors.Count);

        foreach (var cap in depthCaps)
        {
            foreach (var floor in floors)
            {
                var count = 0;

                foreach (var node in rollup.Nodes.Values)
                {
                    if (node.Depth <= cap && node.SubtreeBytes >= floor)
                    {
                        count++;
                    }
                }

                cells.Add(new HistogramCell(cap, floor, count));
            }
        }

        return cells;
    }
}
