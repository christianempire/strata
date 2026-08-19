using Strata.Core.Model;
using Strata.Core.Scanning;

namespace Strata.Tests.Fakes;

/// <summary>
/// An in-memory filesystem standing in for Strata.Platform.
/// </summary>
/// <remarks>
/// This is what makes the scanner's interesting behaviour testable at all: hardlinks, junction
/// cycles, access denials and mid-scan deletions are all awkward to arrange on a real disk and
/// trivial to arrange here. Deliberately thread-safe, because the scanner's whole point is that it
/// enumerates concurrently.
/// </remarks>
internal sealed class FakeDirectoryEnumerator : IDirectoryEnumerator
{
    private readonly Dictionary<string, List<DirectoryEntry>> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DirectoryEnumerationError> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _directoryIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private long _nextId = 1000;

    internal int BackgroundScopesEntered { get; private set; }

    internal string Root { get; }

    internal FakeDirectoryEnumerator(string root = @"\\?\T:\")
    {
        Root = root;
        _directoryIds[root] = 5; // NTFS gives the volume root file ID 5.
        _directories[root] = [];
    }

    /// <summary>Adds a directory. <paramref name="path"/> is relative to the root, backslash-separated.</summary>
    internal long AddDirectory(string path, bool reparsePoint = false, long? explicitId = null)
    {
        var full = Combine(Root, path);
        var id = explicitId ?? Interlocked.Increment(ref _nextId);

        lock (_gate)
        {
            _directoryIds[full] = id;
            _directories.TryAdd(full, []);

            var (parent, name) = Split(full);
            EnsureDirectoryList(parent).Add(new DirectoryEntry(
                id,
                name,
                0,
                0,
                EntryTraits.Directory | (reparsePoint ? EntryTraits.ReparsePoint : EntryTraits.None),
                reparsePoint ? 0x410u : 0x10u,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        }

        return id;
    }

    /// <summary>
    /// Adds a file. Reusing an <paramref name="explicitId"/> already present elsewhere is how a
    /// hardlink is expressed, which mirrors reality: directory records carry no link count, so a
    /// repeated file ID is the only signal there is.
    /// </summary>
    internal long AddFile(string path, long logicalSize, long? sizeOnDisk = null, long? explicitId = null)
    {
        var full = Combine(Root, path);
        var id = explicitId ?? Interlocked.Increment(ref _nextId);
        var (parent, name) = Split(full);

        lock (_gate)
        {
            EnsureDirectoryList(parent).Add(new DirectoryEntry(
                id,
                name,
                logicalSize,
                sizeOnDisk ?? logicalSize,
                EntryTraits.None,
                0x80u,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        }

        return id;
    }

    /// <summary>Makes a directory fail to open, as an ACL or a mid-scan deletion would.</summary>
    internal void FailOn(string path, DirectoryEnumerationError error)
    {
        lock (_gate)
        {
            _failures[Combine(Root, path)] = error;
        }
    }

    public bool TryEnumerate(
        string fullPath,
        List<DirectoryEntry> buffer,
        out DirectoryEnumerationError failure,
        out int nativeErrorCode)
    {
        buffer.Clear();
        nativeErrorCode = 0;

        lock (_gate)
        {
            if (_failures.TryGetValue(fullPath, out var configured))
            {
                failure = configured;
                nativeErrorCode = 5;
                return false;
            }

            if (!_directories.TryGetValue(fullPath, out var entries))
            {
                failure = DirectoryEnumerationError.NotFound;
                return false;
            }

            buffer.AddRange(entries);
        }

        failure = DirectoryEnumerationError.None;
        return true;
    }

    public IDisposable EnterBackgroundIoScope()
    {
        lock (_gate)
        {
            BackgroundScopesEntered++;
        }

        return new NoopScope();
    }

    public bool TryGetDirectoryId(string fullPath, out long fileId)
    {
        lock (_gate)
        {
            return _directoryIds.TryGetValue(fullPath, out fileId);
        }
    }

    private List<DirectoryEntry> EnsureDirectoryList(string path)
    {
        if (!_directories.TryGetValue(path, out var list))
        {
            list = [];
            _directories[path] = list;
        }

        return list;
    }

    private static string Combine(string root, string relative)
        => relative.Length == 0 ? root : root.TrimEnd('\\') + '\\' + relative.Trim('\\');

    private (string Parent, string Name) Split(string full)
    {
        var index = full.LastIndexOf('\\');
        var name = full[(index + 1)..];
        var parent = full[..(index + 1)];

        // The root keeps its trailing separator; everything else is stored without one, matching
        // how the scanner builds child paths.
        if (!string.Equals(parent, Root, StringComparison.OrdinalIgnoreCase))
        {
            parent = parent.TrimEnd('\\');
        }

        return (parent, name);
    }

    private sealed class NoopScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
