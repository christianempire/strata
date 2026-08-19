using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Strata.Storage;

/// <summary>One directory's size through time, sampled onto the shared bucket edges.</summary>
public sealed record DirectorySeries
{
    public required long DirId { get; init; }

    public required string Name { get; init; }

    /// <summary>Subtree bytes at each bucket edge. Same length as the response's bucket list.</summary>
    public required long[] Bytes { get; init; }

    /// <summary>Size at the last edge, which is what the series is ranked and coloured by.</summary>
    public long Latest => Bytes.Length == 0 ? 0 : Bytes[^1];
}

/// <summary>The stacked area the Timeline draws, and the figures around it.</summary>
public sealed record VolumeSeries
{
    public required string FromUtc { get; init; }

    public required string ToUtc { get; init; }

    /// <summary>Bucket edges, ISO-8601 UTC. Every array in this response is indexed by these.</summary>
    public required string[] Buckets { get; init; }

    /// <summary>The named bands, largest last so the stack builds in a stable order.</summary>
    public required IReadOnlyList<DirectorySeries> Series { get; init; }

    /// <summary>Everything past the twelfth directory, summed. Drawn in the one desaturated band.</summary>
    public required long[] Other { get; init; }

    /// <summary>
    /// Used space as the OS reported it, per bucket.
    /// </summary>
    /// <remarks>
    /// Carried so the chart can draw the difference between this and the sum of the bands as a
    /// hatched top band rather than quietly leaving it out. On an unelevated scan of C: that gap is
    /// about 30 GB, and a stack that added up to less than the disk with no explanation would read
    /// as a bug.
    /// </remarks>
    public required long[] Used { get; init; }

    /// <summary>Whether any bucket was measured at all. False means an empty range, not an error.</summary>
    public bool Measured { get; init; }
}

/// <summary>
/// The Timeline's series query.
/// </summary>
/// <remarks>
/// This is the query <c>dir_history</c> was clustered for. Its primary key is
/// (volume, directory, time), so one directory's whole history is contiguous and a window over it
/// is a range seek rather than a scan — which is why migration 005 deliberately left the table with
/// no secondary index on time.
/// <para>
/// One query per top-level directory, then bucketed in memory. The alternative, a query per bucket,
/// is sixty seeks per directory instead of one range read, and the alternative to that — walking the
/// tree once per snapshot — is 672 tree walks for a week at a fifteen-minute cadence.
/// </para>
/// </remarks>
public static class SeriesQueries
{
    /// <summary>How many directories get their own band before the rest fold into "other".</summary>
    /// <remarks>Twelve, because the palette's categorical ramp has twelve distinguishable hues.</remarks>
    public const int DefaultTopCount = 12;

    public static VolumeSeries Load(
        SqliteConnection connection,
        long volumeId,
        long volumeTotalBytes,
        DateTime fromUtc,
        DateTime toUtc,
        int buckets,
        int topCount = DefaultTopCount)
    {
        ArgumentNullException.ThrowIfNull(connection);

        buckets = Math.Clamp(buckets, 2, 720);
        topCount = Math.Clamp(topCount, 1, 24);

        var edges = Edges(fromUtc, toUtc, buckets);
        var children = DirectoryHistoryQueries.ChildrenOf(
            connection, volumeId, DirectoryHistoryQueries.RootId(connection, volumeId) ?? -1);

        var sampled = new List<DirectorySeries>(children.Count);

        foreach (var child in children)
        {
            var values = Sample(connection, volumeId, child.Id, edges);

            // A directory that was empty for the whole window is not a band, it is twelve pixels of
            // nothing taking a hue another directory could have used.
            if (values[^1] > 0 || Array.Exists(values, v => v > 0))
            {
                sampled.Add(new DirectorySeries
                {
                    DirId = child.Id,
                    Name = child.Name,
                    Bytes = values,
                });
            }
        }

        // Ranked by size at the end of the window rather than by mean: the question the chart
        // answers is what is on the disk now and how it got that way.
        sampled.Sort((a, b) => b.Latest.CompareTo(a.Latest));

        var named = sampled.Count > topCount ? sampled.GetRange(0, topCount) : sampled;
        var other = new long[edges.Length];

        for (var i = topCount; i < sampled.Count; i++)
        {
            for (var b = 0; b < edges.Length; b++)
            {
                other[b] += sampled[i].Bytes[b];
            }
        }

        var (used, measured) = UsedSpace(connection, volumeId, volumeTotalBytes, edges);

        return new VolumeSeries
        {
            FromUtc = fromUtc.ToString("O"),
            ToUtc = toUtc.ToString("O"),
            Buckets = Array.ConvertAll(edges, e => DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime.ToString("O")),

            // Smallest first, so the stack builds with the largest band at the bottom once the
            // client reverses it — and so a band's colour does not move when a sibling overtakes it.
            Series = named,
            Other = other,
            Used = used,
            Measured = measured,
        };
    }

    private static long[] Edges(DateTime fromUtc, DateTime toUtc, int buckets)
    {
        var from = Unix(fromUtc);
        var to = Unix(toUtc);

        if (to <= from)
        {
            return [from];
        }

        var edges = new long[buckets];
        var step = (double)(to - from) / (buckets - 1);

        for (var i = 0; i < buckets; i++)
        {
            edges[i] = from + (long)Math.Round(step * i);
        }

        edges[^1] = to;

        return edges;
    }

    /// <summary>
    /// One directory's size at each bucket edge.
    /// </summary>
    /// <remarks>
    /// Reads the window in one range seek plus the single opening row before it, then walks the
    /// edges and the rows together. "Size at T" is the latest row at or before T, so a directory
    /// that did not change holds its value across as many buckets as it needs to — which is what
    /// makes a sparse history drawable as a continuous band.
    /// <para>
    /// The opening row matters more than it looks. Without it a quiet directory has no row inside
    /// the window at all and would be drawn as zero until its first change, which is the difference
    /// between "Program Files was 92 GB all week" and "Program Files appeared on Thursday".
    /// </para>
    /// </remarks>
    private static long[] Sample(SqliteConnection connection, long volumeId, long dirId, long[] edges)
    {
        var values = new long[edges.Length];

        using var opening = connection.CreateCommand();
        opening.CommandText = """
            SELECT subtree_bytes FROM dir_history
            WHERE volume_id = $v AND dir_id = $d AND taken_unix <= $t
            ORDER BY taken_unix DESC LIMIT 1;
            """;

        opening.Parameters.AddWithValue("$v", volumeId);
        opening.Parameters.AddWithValue("$d", dirId);
        opening.Parameters.AddWithValue("$t", edges[0]);

        var current = opening.ExecuteScalar() is long bytes ? bytes : 0L;

        using var window = connection.CreateCommand();
        window.CommandText = """
            SELECT taken_unix, subtree_bytes FROM dir_history
            WHERE volume_id = $v AND dir_id = $d AND taken_unix > $from AND taken_unix <= $to
            ORDER BY taken_unix;
            """;

        window.Parameters.AddWithValue("$v", volumeId);
        window.Parameters.AddWithValue("$d", dirId);
        window.Parameters.AddWithValue("$from", edges[0]);
        window.Parameters.AddWithValue("$to", edges[^1]);

        using var reader = window.ExecuteReader();

        var hasRow = reader.Read();

        for (var i = 0; i < edges.Length; i++)
        {
            while (hasRow && reader.GetInt64(0) <= edges[i])
            {
                current = reader.GetInt64(1);
                hasRow = reader.Read();
            }

            values[i] = current;
        }

        return values;
    }

    /// <summary>
    /// Used space per bucket, from the free space every snapshot records.
    /// </summary>
    /// <remarks>
    /// Capacity minus free rather than Strata's own total, because the point of this band is to show
    /// the difference between the two. Buckets before the first snapshot in range carry zero and are
    /// reported as unmeasured, so an empty window is drawn as a gap rather than as a volume that was
    /// briefly empty.
    /// </remarks>
    private static (long[] Used, bool Measured) UsedSpace(
        SqliteConnection connection,
        long volumeId,
        long volumeTotalBytes,
        long[] edges)
    {
        var used = new long[edges.Length];

        using var opening = connection.CreateCommand();
        opening.CommandText = """
            SELECT free_bytes, total_bytes FROM snapshots
            WHERE volume_id = $v AND taken_utc <= $t
            ORDER BY taken_utc DESC LIMIT 1;
            """;

        opening.Parameters.AddWithValue("$v", volumeId);
        opening.Parameters.AddWithValue("$t", Iso(edges[0]));

        var current = 0L;
        var measured = false;

        using (var reader = opening.ExecuteReader())
        {
            if (reader.Read())
            {
                current = Used(reader.GetInt64(0), reader.GetInt64(1), volumeTotalBytes);
                measured = true;
            }
        }

        using var window = connection.CreateCommand();
        window.CommandText = """
            SELECT taken_utc, free_bytes, total_bytes FROM snapshots
            WHERE volume_id = $v AND taken_utc > $from AND taken_utc <= $to
            ORDER BY taken_utc;
            """;

        window.Parameters.AddWithValue("$v", volumeId);
        window.Parameters.AddWithValue("$from", Iso(edges[0]));
        window.Parameters.AddWithValue("$to", Iso(edges[^1]));

        using var rows = window.ExecuteReader();

        var hasRow = rows.Read();

        for (var i = 0; i < edges.Length; i++)
        {
            while (hasRow && ParseUnix(rows.GetString(0)) <= edges[i])
            {
                current = Used(rows.GetInt64(1), rows.GetInt64(2), volumeTotalBytes);
                measured = true;
                hasRow = rows.Read();
            }

            used[i] = current;
        }

        return (used, measured);
    }

    /// <summary>
    /// Used space at a snapshot, preferring what the OS said over what Strata counted.
    /// </summary>
    /// <remarks>
    /// Falls back to Strata's own total when free space was not recorded, which is the only case
    /// where the hatched gap collapses to nothing. Better than reporting the whole capacity as used.
    /// </remarks>
    private static long Used(long freeBytes, long totalBytes, long volumeTotalBytes)
        => freeBytes > 0 && volumeTotalBytes > freeBytes ? volumeTotalBytes - freeBytes : totalBytes;

    private static string Iso(long unix)
        => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("O");

    private static long ParseUnix(string isoUtc)
        => DateTime.TryParse(
            isoUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero).ToUnixTimeSeconds()
            : 0;

    private static long Unix(DateTime value)
        => new DateTimeOffset(
            value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
