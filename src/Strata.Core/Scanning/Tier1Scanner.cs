using System.Collections.Concurrent;
using Strata.Core.Model;
using Strata.Core.Rollup;

namespace Strata.Core.Scanning;

/// <summary>
/// Full baseline scan: enumerates an entire volume and records every file.
/// </summary>
/// <remarks>
/// Runs rarely — first sight of a volume, or after the change journal has been invalidated — which
/// is what makes its cost acceptable. Everything about the design assumes that: it is optimised for
/// throughput under a low I/O priority, not for being interruptible or incremental.
/// <para>
/// Lives in Core with no I/O of its own. All filesystem access goes through
/// <see cref="IDirectoryEnumerator"/>, so the parallel walk, the reparse-point rules, the hardlink
/// deduplication and the completion detection can all be tested against an in-memory tree.
/// </para>
/// </remarks>
public sealed class Tier1Scanner : IVolumeScanner
{
    private readonly IDirectoryEnumerator _enumerator;
    private readonly ScanOptions _options;

    public Tier1Scanner(IDirectoryEnumerator enumerator, ScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(enumerator);

        _enumerator = enumerator;
        _options = options ?? new ScanOptions();
    }

    public ScanTier Tier => ScanTier.Full;

    /// <summary>A full walk works on any filesystem, so this is only gated on the volume being local.</summary>
    public bool CanScan(VolumeIdentity volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return volume.Kind is VolumeKind.Fixed or VolumeKind.RamDisk;
    }

    public ScanResult Scan(VolumeIdentity volume, IScanSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(sink);

        var startedUtc = DateTime.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var root = volume.RootPath;

        if (!_enumerator.TryGetDirectoryId(root, out var rootId))
        {
            throw new IOException($"Could not open the root of {volume.Display} ({root}) to read its file ID.");
        }

        using var state = new ScanState(_options, sink, cancellationToken);
        var rootNode = new DirectoryNode(rootId, rootId, string.Empty) { Depth = 0 };
        state.Nodes[rootId] = rootNode;

        state.Enqueue(new PendingDirectory(rootId, root));

        RunWorkers(state);

        cancellationToken.ThrowIfCancellationRequested();

        // Copy out of the concurrent dictionary: the rollup is single-threaded from here, and a
        // plain dictionary is both faster to iterate and the shape the engine takes.
        var nodes = new Dictionary<long, DirectoryNode>(state.Nodes.Count);
        foreach (var pair in state.Nodes)
        {
            nodes[pair.Key] = pair.Value;
        }

        var rollup = RollupEngine.Compute(nodes, rootId);
        stopwatch.Stop();

        return new ScanResult
        {
            Volume = volume,
            Tier = ScanTier.Full,
            StartedUtc = startedUtc,
            Duration = stopwatch.Elapsed,
            Rollup = rollup,
            FileCount = rollup.TotalFileCount,
            DirectoryCount = rollup.TotalDirCount,
            TotalBytes = rollup.TotalBytes,
            TotalSizeOnDisk = rollup.TotalSizeOnDisk,
            HardLinkCount = state.HardLinkCount,
            HardLinkBytesSaved = state.HardLinkBytesSaved,
            ReparsePointCount = state.ReparsePointCount,
            Inaccessible = state.SnapshotInaccessible(),
        };
    }

    /// <summary>
    /// Starts the workers and blocks until the queue drains.
    /// </summary>
    /// <remarks>
    /// Dedicated threads rather than the thread pool, because each worker puts itself into
    /// background I/O mode for its lifetime. A pooled thread left in background mode and handed back
    /// to the pool would silently throttle unrelated work — including, from Phase 3, the API serving
    /// the UI. See docs/DESIGN-REVIEW.md section A3.
    /// </remarks>
    private void RunWorkers(ScanState state)
    {
        var workerCount = Math.Max(1, _options.DegreeOfParallelism);
        var threads = new Thread[workerCount];
        var failures = new ConcurrentBag<Exception>();

        for (var i = 0; i < workerCount; i++)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Worker(state);
                }
                catch (OperationCanceledException)
                {
                    // Expected on cancellation; the queue is completed below so peers unblock too.
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    state.AbortQueue();
                }
            })
            {
                IsBackground = true,
                Name = $"strata-scan-{i}",
            };

            threads[i] = thread;
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        if (!failures.IsEmpty)
        {
            throw new AggregateException("One or more scan workers failed.", failures);
        }
    }

    private void Worker(ScanState state)
    {
        using var ioScope = _options.UseBackgroundIoPriority
            ? _enumerator.EnterBackgroundIoScope()
            : NullScope.Instance;

        // Per-worker, reused for the whole scan: a full volume walk visits hundreds of thousands of
        // directories, and allocating a collection per directory would dominate everything else.
        var entries = new List<DirectoryEntry>(256);
        var batch = new FileRecord[_options.WriteBatchSize];
        var batchCount = 0;
        var pendingIds = new List<long>(256);

        while (state.TryTake(out var pending))
        {
            try
            {
                ProcessDirectory(state, pending, entries, pendingIds, batch, ref batchCount);
            }
            finally
            {
                state.CompleteOne();
            }
        }

        if (batchCount > 0)
        {
            state.Sink.Write(batch.AsSpan(0, batchCount));
        }
    }

    private void ProcessDirectory(
        ScanState state,
        PendingDirectory pending,
        List<DirectoryEntry> entries,
        List<long> pendingIds,
        FileRecord[] batch,
        ref int batchCount)
    {
        entries.Clear();

        if (!_enumerator.TryEnumerate(pending.FullPath, entries, out var failure, out _))
        {
            state.RecordInaccessible(failure);
            return;
        }

        var node = state.Nodes[pending.Id];

        long ownBytes = 0;
        long ownSizeOnDisk = 0;
        var ownFileCount = 0;

        // Two passes so the hardlink lock is taken once per directory rather than once per file.
        // Directories average a handful of files, so this is roughly an order of magnitude fewer
        // lock acquisitions over a full scan.
        pendingIds.Clear();

        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            pendingIds.Add(entry.FileId);
        }

        var linkFlags = state.ClassifyHardLinks(pendingIds);

        var fileIndex = 0;

        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                HandleSubdirectory(state, pending, entry);
                continue;
            }

            var isHardLink = linkFlags is not null && linkFlags[fileIndex];
            fileIndex++;

            if (isHardLink)
            {
                state.RecordHardLink(entry.LogicalSize);
            }

            var record = new FileRecord(
                entry.FileId,
                pending.Id,
                entry.Name,
                entry.LogicalSize,
                entry.SizeOnDisk,
                entry.RawAttributes,
                entry.LastWriteUtc,
                isHardLink);

            ownBytes += record.CountedBytes;
            ownSizeOnDisk += record.CountedSizeOnDisk;
            ownFileCount++;

            batch[batchCount++] = record;

            if (batchCount == batch.Length)
            {
                state.Sink.Write(batch.AsSpan(0, batchCount));
                batchCount = 0;
            }
        }

        node.OwnBytes = ownBytes;
        node.OwnSizeOnDisk = ownSizeOnDisk;
        node.OwnFileCount = ownFileCount;

        state.RecordProgress(ownFileCount, ownBytes, pending.FullPath);
    }

    private void HandleSubdirectory(ScanState state, PendingDirectory parent, in DirectoryEntry entry)
    {
        if (entry.Name is "." or "..")
        {
            return;
        }

        if (IsExcluded(entry.Name))
        {
            return;
        }

        var child = new DirectoryNode(entry.FileId, parent.Id, entry.Name)
        {
            IsReparsePoint = entry.IsReparsePoint,
        };

        // TryAdd rather than an assignment: a file ID we have already recorded means we have reached
        // this directory by a second route, which is the cycle we would otherwise walk forever.
        if (!state.Nodes.TryAdd(entry.FileId, child))
        {
            return;
        }

        if (entry.IsReparsePoint)
        {
            // Recorded as a link and deliberately not descended into. Following junctions and
            // symlinks risks both infinite recursion and attributing the same bytes twice on a
            // volume where they have already been counted at their real location.
            state.RecordReparsePoint();
            return;
        }

        state.Enqueue(new PendingDirectory(entry.FileId, Combine(parent.FullPath, entry.Name)));
    }

    private bool IsExcluded(string name)
    {
        var excluded = _options.ExcludedDirectoryNames;

        for (var i = 0; i < excluded.Count; i++)
        {
            if (string.Equals(excluded[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Joins a directory path to a child name. The volume root already ends in a separator; nothing
    /// else does.
    /// </summary>
    private static string Combine(string parent, string name)
        => parent.EndsWith('\\') ? parent + name : parent + '\\' + name;

    private readonly record struct PendingDirectory(long Id, string FullPath);

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>Shared mutable state for one scan. Every member is safe to touch from any worker.</summary>
    private sealed class ScanState : IDisposable
    {
        private readonly ScanOptions _options;
        private readonly CancellationToken _cancellationToken;
        private readonly BlockingCollection<PendingDirectory> _queue = new(new ConcurrentQueue<PendingDirectory>());
        private readonly int[] _inaccessible = new int[Enum.GetValues<DirectoryEnumerationError>().Length];

        /// <summary>
        /// Every file ID seen so far, used to spot the second and later links to the same extents.
        /// Directory records carry no link count, so seeing an ID twice is the only signal available.
        /// </summary>
        private readonly HashSet<long>? _seenFileIds;

        private readonly Lock _seenLock = new();

        private bool[] _linkScratch = new bool[256];

        /// <summary>
        /// Directories discovered but not yet finished. Completion cannot be detected by the queue
        /// being empty — mid-scan it is empty constantly, while workers are busy producing more work
        /// — so it is detected by this reaching zero.
        /// </summary>
        private int _outstanding;

        private long _directoriesCompleted;
        private long _filesSeen;
        private long _bytesSeen;
        private long _hardLinkCount;
        private long _hardLinkBytesSaved;
        private int _reparsePointCount;
        private long _nextProgressAt;
        private long _lastProgressTicks = Environment.TickCount64;

        internal ScanState(ScanOptions options, IScanSink sink, CancellationToken cancellationToken)
        {
            _options = options;
            _cancellationToken = cancellationToken;
            Sink = sink;
            _nextProgressAt = options.ProgressIntervalDirectories;

            if (options.DeduplicateHardLinks)
            {
                _seenFileIds = [];
            }
        }

        internal IScanSink Sink { get; }

        internal ConcurrentDictionary<long, DirectoryNode> Nodes { get; } = new();

        internal long HardLinkCount => Interlocked.Read(ref _hardLinkCount);

        internal long HardLinkBytesSaved => Interlocked.Read(ref _hardLinkBytesSaved);

        internal int ReparsePointCount => Volatile.Read(ref _reparsePointCount);

        internal void Enqueue(PendingDirectory directory)
        {
            // Counted before being queued, so the count can never dip to zero while work is in flight.
            Interlocked.Increment(ref _outstanding);

            try
            {
                _queue.Add(directory, _cancellationToken);
            }
            catch (Exception) when (CompleteFailedEnqueue())
            {
                throw;
            }
        }

        private bool CompleteFailedEnqueue()
        {
            CompleteOne();
            return false;
        }

        internal bool TryTake(out PendingDirectory directory)
        {
            try
            {
                return _queue.TryTake(out directory, Timeout.Infinite, _cancellationToken);
            }
            catch (OperationCanceledException)
            {
                directory = default;
                return false;
            }
            catch (ObjectDisposedException)
            {
                directory = default;
                return false;
            }
        }

        internal void CompleteOne()
        {
            if (Interlocked.Decrement(ref _outstanding) == 0)
            {
                _queue.CompleteAdding();
            }
        }

        internal void AbortQueue() => _queue.CompleteAdding();

        public void Dispose() => _queue.Dispose();

        /// <summary>
        /// Decides, for one directory's files, which are additional links to bytes already counted.
        /// </summary>
        /// <returns>
        /// A parallel array of flags, or null when deduplication is off. The array is scratch space
        /// reused across calls and is only valid until the next call on this worker.
        /// </returns>
        internal bool[]? ClassifyHardLinks(List<long> fileIds)
        {
            if (_seenFileIds is null)
            {
                return null;
            }

            if (_linkScratch.Length < fileIds.Count)
            {
                _linkScratch = new bool[Math.Max(fileIds.Count, _linkScratch.Length * 2)];
            }

            lock (_seenLock)
            {
                for (var i = 0; i < fileIds.Count; i++)
                {
                    _linkScratch[i] = !_seenFileIds.Add(fileIds[i]);
                }
            }

            return _linkScratch;
        }

        internal void RecordHardLink(long bytes)
        {
            Interlocked.Increment(ref _hardLinkCount);
            Interlocked.Add(ref _hardLinkBytesSaved, bytes);
        }

        internal void RecordReparsePoint() => Interlocked.Increment(ref _reparsePointCount);

        internal void RecordInaccessible(DirectoryEnumerationError error)
            => Interlocked.Increment(ref _inaccessible[(int)error]);

        internal void RecordProgress(int files, long bytes, string path)
        {
            var completed = Interlocked.Increment(ref _directoriesCompleted);
            Interlocked.Add(ref _filesSeen, files);
            var totalBytes = Interlocked.Add(ref _bytesSeen, bytes);

            var callback = _options.OnProgress;

            if (callback is null)
            {
                return;
            }

            // Either enough directories or enough time. Whichever comes first, because at the top of
            // a volume the count arrives slowly and at the bottom it arrives in floods, and a
            // display that goes quiet for twenty seconds reads as a hang rather than as a wide
            // directory.
            var now = Environment.TickCount64;
            var byCount = completed >= Interlocked.Read(ref _nextProgressAt);
            var byTime = now - Interlocked.Read(ref _lastProgressTicks) >= _options.ProgressIntervalMilliseconds;

            if (!byCount && !byTime)
            {
                return;
            }

            Interlocked.Exchange(ref _nextProgressAt, completed + _options.ProgressIntervalDirectories);
            Interlocked.Exchange(ref _lastProgressTicks, now);

            callback(new ScanProgress(completed, Interlocked.Read(ref _filesSeen), totalBytes, path));
        }

        internal Dictionary<DirectoryEnumerationError, int> SnapshotInaccessible()
        {
            var result = new Dictionary<DirectoryEnumerationError, int>();

            for (var i = 0; i < _inaccessible.Length; i++)
            {
                var error = (DirectoryEnumerationError)i;

                if (error != DirectoryEnumerationError.None && _inaccessible[i] > 0)
                {
                    result[error] = _inaccessible[i];
                }
            }

            return result;
        }
    }
}
