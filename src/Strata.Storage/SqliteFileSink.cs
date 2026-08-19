using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Strata.Core.Model;
using Strata.Core.Scanning;

namespace Strata.Storage;

/// <summary>
/// Streams scanned files into the <c>files</c> table.
/// </summary>
/// <remarks>
/// Scan workers hand batches to a single dedicated writer thread rather than writing directly.
/// A SQLite connection is not thread-safe, and simply locking around the insert would stall four
/// enumeration threads behind whichever one currently holds the connection. Handing off keeps the
/// scanners enumerating while the writer commits.
/// <para>
/// The queue is bounded: if the disk cannot keep up with enumeration, the right behaviour is to slow
/// the scan down, not to accumulate millions of pending rows in memory.
/// </para>
/// </remarks>
public sealed class SqliteFileSink : IScanSink, IDisposable
{
    /// <summary>
    /// Rows per transaction. One transaction for the whole scan would be fastest but would grow the
    /// write-ahead log to the size of the entire table before a single commit.
    /// </summary>
    private const int RowsPerTransaction = 200_000;

    private const int MaxQueuedBatches = 64;

    private readonly SqliteConnection _connection;
    private readonly long _volumeId;
    private readonly BlockingCollection<PooledBatch> _queue = new(MaxQueuedBatches);
    private readonly Thread _writer;

    private Exception? _writerFailure;
    private long _rowsWritten;
    private bool _disposed;

    public SqliteFileSink(SqliteConnection connection, long volumeId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _volumeId = volumeId;

        // A Tier 1 scan is the authoritative view of the volume, so anything recorded previously is
        // replaced wholesale rather than merged.
        ClearVolume();

        _writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "strata-sqlite-writer",
        };

        _writer.Start();
    }

    public long RowsWritten => Interlocked.Read(ref _rowsWritten);

    public void Write(ReadOnlySpan<FileRecord> batch)
    {
        if (batch.IsEmpty)
        {
            return;
        }

        // The caller reuses its buffer immediately, so the records have to be copied before the
        // handoff. Pooled, because this happens once per few thousand files for a whole scan.
        var array = ArrayPool<FileRecord>.Shared.Rent(batch.Length);
        batch.CopyTo(array);

        try
        {
            _queue.Add(new PooledBatch(array, batch.Length));
        }
        catch (InvalidOperationException)
        {
            // Adding completed, which means the writer has already failed and is shutting down.
            ArrayPool<FileRecord>.Shared.Return(array);
            throw WriterFailure();
        }
    }

    private void WriterLoop()
    {
        try
        {
            var transaction = _connection.BeginTransaction();

            try
            {
                using var command = CreateInsertCommand(transaction);
                var sinceCommit = 0;

                foreach (var batch in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        for (var i = 0; i < batch.Count; i++)
                        {
                            Bind(command, batch.Array[i]);
                            command.ExecuteNonQuery();
                        }

                        sinceCommit += batch.Count;
                        Interlocked.Add(ref _rowsWritten, batch.Count);
                    }
                    finally
                    {
                        ArrayPool<FileRecord>.Shared.Return(batch.Array);
                    }

                    if (sinceCommit >= RowsPerTransaction)
                    {
                        transaction.Commit();
                        transaction.Dispose();
                        transaction = _connection.BeginTransaction();
                        command.Transaction = transaction;
                        sinceCommit = 0;
                    }
                }

                transaction.Commit();
            }
            finally
            {
                transaction.Dispose();
            }
        }
        catch (Exception ex)
        {
            _writerFailure = ex;
            _queue.CompleteAdding();

            // Drain so producers blocked on a full queue can fail fast rather than deadlock.
            while (_queue.TryTake(out var abandoned))
            {
                ArrayPool<FileRecord>.Shared.Return(abandoned.Array);
            }
        }
    }

    /// <summary>
    /// Signals that no more records are coming and waits for the writer to finish committing.
    /// </summary>
    public void Complete()
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.CompleteAdding();
        }

        _writer.Join();

        if (_writerFailure is not null)
        {
            throw WriterFailure();
        }
    }

    private InvalidOperationException WriterFailure()
        => new("The database writer failed while a scan was in progress.", _writerFailure);

    private SqliteCommand CreateInsertCommand(SqliteTransaction transaction)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;

        // One row per directory entry, so every hardlink gets its own row. A plain INSERT is safe:
        // the primary key is (volume_id, parent_id, name), and a directory cannot hold two entries
        // with the same name. A conflict here would mean the scanner walked a directory twice,
        // which is a bug worth surfacing rather than papering over.
        command.CommandText = """
            INSERT INTO files
                (volume_id, parent_id, name, file_id, logical_size, size_on_disk,
                 attributes, modified_unix, is_hard_link)
            VALUES ($volume, $parent, $name, $file, $logical, $ondisk, $attr, $modified, $link);
            """;

        // Parameters are created once and their values reassigned per row. Building parameter
        // objects per row would dominate the cost of a multi-million-row insert.
        command.Parameters.Add("$volume", SqliteType.Integer).Value = _volumeId;
        command.Parameters.Add("$parent", SqliteType.Integer);
        command.Parameters.Add("$name", SqliteType.Text);
        command.Parameters.Add("$file", SqliteType.Integer);
        command.Parameters.Add("$logical", SqliteType.Integer);
        command.Parameters.Add("$ondisk", SqliteType.Integer);
        command.Parameters.Add("$attr", SqliteType.Integer);
        command.Parameters.Add("$modified", SqliteType.Integer);
        command.Parameters.Add("$link", SqliteType.Integer);

        command.Prepare();
        return command;
    }

    private static void Bind(SqliteCommand command, in FileRecord record)
    {
        var parameters = command.Parameters;
        parameters[1].Value = record.ParentId;
        parameters[2].Value = record.Name;
        parameters[3].Value = record.FileId;
        parameters[4].Value = record.LogicalSize;
        parameters[5].Value = record.SizeOnDisk;
        parameters[6].Value = record.RawAttributes;
        parameters[7].Value = ToUnixSeconds(record.LastWriteUtc);
        parameters[8].Value = record.IsHardLink ? 1 : 0;
    }

    /// <summary>
    /// Converts to Unix seconds, clamping timestamps that predate the epoch.
    /// </summary>
    /// <remarks>
    /// Real volumes carry a handful of files with nonsense timestamps. Second resolution is ample
    /// here: this field answers "when did this last change", not "in what order within a second".
    /// </remarks>
    private static long ToUnixSeconds(DateTime value)
        => value <= DateTime.UnixEpoch ? 0 : new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds();

    private void ClearVolume()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM files WHERE volume_id = $volume;";
        command.Parameters.AddWithValue("$volume", _volumeId);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_queue.IsAddingCompleted)
        {
            _queue.CompleteAdding();
        }

        if (_writer.IsAlive)
        {
            _writer.Join();
        }

        _queue.Dispose();
    }

    private readonly record struct PooledBatch(FileRecord[] Array, int Count);
}
