using System.Globalization;
using Strata.Core.Diffing;

namespace Strata.Host.Scanning;

/// <summary>
/// Appends every directory whose subtree total moved, per tick, to a CSV.
/// </summary>
/// <remarks>
/// A measurement instrument, not a product feature. The question it exists to answer is what a
/// sparse directory history would actually cost: today <c>snapshot_dirs</c> writes all 5,740
/// qualifying rows every tick regardless of what changed, and the alternative is to write a row only
/// when a directory has drifted past a threshold. Sizing that needs the real per-tick fan-out at
/// unbounded depth, and there is no way to get it except from real activity on a real machine.
/// <para>
/// Raw rather than pre-aggregated on purpose: a dead-band is a stateful filter, so the tick that
/// crosses the threshold depends on every tick before it. Only a full log can be replayed against
/// several thresholds afterwards, and replaying is the whole point.
/// </para>
/// </remarks>
internal sealed class RollupLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    internal RollupLog(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var existed = File.Exists(path) && new FileInfo(path).Length > 0;

        _writer = new StreamWriter(path, append: true) { AutoFlush = true };

        if (!existed)
        {
            _writer.WriteLine("tick_utc,volume,dir_id,delta_bytes,file_delta,seed");
        }
    }

    /// <summary>
    /// Records one tick's fan-out.
    /// </summary>
    /// <param name="seeds">
    /// Directories that changed directly, as opposed to the ancestors that inherited the change.
    /// Kept as a flag per row so the fan-out ratio can be read straight out of the log.
    /// </param>
    internal void Write(
        DateTime tickUtc,
        string volume,
        IReadOnlyDictionary<long, DirectoryDelta> touched,
        IReadOnlyDictionary<long, DirectoryDelta> seeds)
    {
        if (touched.Count == 0)
        {
            return;
        }

        var stamp = tickUtc.ToString("O", CultureInfo.InvariantCulture);

        lock (_gate)
        {
            foreach (var (dirId, delta) in touched)
            {
                _writer.Write(stamp);
                _writer.Write(',');
                _writer.Write(volume);
                _writer.Write(',');
                _writer.Write(dirId.ToString(CultureInfo.InvariantCulture));
                _writer.Write(',');
                _writer.Write(delta.OwnBytesDelta.ToString(CultureInfo.InvariantCulture));
                _writer.Write(',');
                _writer.Write(delta.FileCountDelta.ToString(CultureInfo.InvariantCulture));
                _writer.Write(',');
                _writer.WriteLine(seeds.ContainsKey(dirId) ? '1' : '0');
            }
        }
    }

    public void Dispose() => _writer.Dispose();
}
