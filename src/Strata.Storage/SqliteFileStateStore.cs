using Microsoft.Data.Sqlite;
using Strata.Core.Scanning;

namespace Strata.Storage;

/// <summary>
/// The recorded state of one volume, backed by SQLite.
/// </summary>
/// <remarks>
/// Every operation is a point lookup or a point write on a primary key, because a change journal
/// record addresses exactly one directory entry by (parent, name) and that is the table's key. The
/// commands are prepared once and reused across a tick, since a busy tick performs thousands of
/// them and command construction would otherwise dominate.
/// <para>
/// Writes are buffered into the caller's transaction; the caller commits. A tick has to be atomic
/// or a crash mid-tick would leave the cursor ahead of the data it describes.
/// </para>
/// </remarks>
public sealed class SqliteFileStateStore : IFileStateStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly long _volumeId;

    private readonly SqliteCommand _selectByKey;
    private readonly SqliteCommand _selectByFileId;
    private readonly SqliteCommand _upsert;
    private readonly SqliteCommand _delete;
    private readonly SqliteCommand _dirExists;
    private readonly SqliteCommand _addDir;
    private readonly SqliteCommand _addDirName;

    private bool _disposed;

    public SqliteFileStateStore(SqliteConnection connection, long volumeId, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _volumeId = volumeId;

        _selectByKey = Prepare(transaction, """
            SELECT parent_id, name, file_id, logical_size, size_on_disk, is_hard_link
            FROM files WHERE volume_id = $v AND parent_id = $p AND name = $n;
            """, Int("$p"), Text("$n"));

        // INDEXED BY is not decoration here. Left to choose, SQLite plans this as
        // "SEARCH files USING PRIMARY KEY (volume_id=?)" - a range scan over every row on the
        // volume - because ix_files_file_id does not cover the size columns and, with no
        // statistics, a scan looks no worse than a seek plus a lookup per match. On the target
        // machine that is 3.67M rows per call: measured at 442 ms against 0.010 ms once the index
        // is forced. A tick doing a few hundred of these took two minutes.
        //
        // Nothing about the symptom points at the cause: the plan for a narrower projection of the
        // same predicate does report a covering index, so the query looks fine in isolation.
        _selectByFileId = Prepare(transaction, """
            SELECT parent_id, name, file_id, logical_size, size_on_disk, is_hard_link
            FROM files INDEXED BY ix_files_file_id
            WHERE volume_id = $v AND file_id = $f;
            """, Int("$f"));

        _upsert = Prepare(transaction, """
            INSERT INTO files
                (volume_id, parent_id, name, file_id, logical_size, size_on_disk,
                 attributes, modified_unix, is_hard_link)
            VALUES ($v, $p, $n, $f, $ls, $sd, 0, $mu, $hl)
            ON CONFLICT (volume_id, parent_id, name) DO UPDATE SET
                file_id       = excluded.file_id,
                logical_size  = excluded.logical_size,
                size_on_disk  = excluded.size_on_disk,
                modified_unix = excluded.modified_unix,
                is_hard_link  = excluded.is_hard_link;
            """, Int("$p"), Text("$n"), Int("$f"), Int("$ls"), Int("$sd"), Int("$mu"), Int("$hl"));

        _delete = Prepare(transaction,
            "DELETE FROM files WHERE volume_id = $v AND parent_id = $p AND name = $n;", Int("$p"), Text("$n"));

        _dirExists = Prepare(transaction,
            "SELECT 1 FROM dir_totals WHERE volume_id = $v AND dir_id = $d LIMIT 1;", Int("$d"));

        // A directory discovered by Tier 2 starts empty; its totals accrue as its contents arrive.
        // Depth is derived from its parent so paths stay reconstructable.
        _addDir = Prepare(transaction, """
            INSERT OR IGNORE INTO dir_totals
                (volume_id, dir_id, parent_id, name, depth, subtree_bytes, subtree_on_disk,
                 subtree_files, subtree_dirs)
            VALUES ($v, $d, $p, $n,
                    COALESCE((SELECT depth + 1 FROM dir_totals WHERE volume_id = $v AND dir_id = $p), 1),
                    0, 0, 0, 0);
            """, Int("$d"), Int("$p"), Text("$n"));

        // The same directory in the name dimension, which outlives dir_totals across a baseline and
        // is what keeps old history nameable. Kept in step here rather than reconciled later,
        // because a directory that changes and is deleted between two baselines would otherwise
        // never be recorded at all.
        _addDirName = Prepare(transaction, """
            INSERT OR IGNORE INTO dir_names
                (volume_id, dir_id, parent_id, name, depth, first_seen_utc)
            VALUES ($v, $d, $p, $n,
                    COALESCE((SELECT depth + 1 FROM dir_totals WHERE volume_id = $v AND dir_id = $p), 1),
                    $t);
            """, Int("$d"), Int("$p"), Text("$n"), Text("$t"));
    }

    private static SqliteParameter Int(string name) => new(name, SqliteType.Integer);

    private static SqliteParameter Text(string name) => new(name, SqliteType.Text);

    /// <summary>
    /// Builds a command whose parameters are declared with the type of the column they compare
    /// against.
    /// </summary>
    /// <remarks>
    /// The declared type is load-bearing, not cosmetic. Binding a file ID as text made
    /// <c>WHERE file_id = $f</c> unable to seek the index, so every lookup degraded into a scan of
    /// all 3.67M index entries: measured at 469 ms per call against 0.002 ms once typed correctly.
    /// A tick doing a few hundred of those took two minutes instead of milliseconds, and nothing
    /// about it looked wrong - the query plan still reported a covering index.
    /// </remarks>
    private SqliteCommand Prepare(SqliteTransaction? transaction, string sql, params SqliteParameter[] parameters)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$v", _volumeId);
        command.Parameters.AddRange(parameters);

        return command;
    }

    public bool TryGetEntry(long parentId, string name, out StoredEntry entry)
    {
        _selectByKey.Parameters["$p"].Value = parentId;
        _selectByKey.Parameters["$n"].Value = name;

        using var reader = _selectByKey.ExecuteReader();

        if (reader.Read())
        {
            entry = Read(reader);
            return true;
        }

        entry = default;
        return false;
    }

    public IReadOnlyList<StoredEntry> GetEntriesByFileId(long fileId)
    {
        _selectByFileId.Parameters["$f"].Value = fileId;

        var results = new List<StoredEntry>(1);
        using var reader = _selectByFileId.ExecuteReader();

        while (reader.Read())
        {
            results.Add(Read(reader));
        }

        return results;
    }

    public void Upsert(in StoredEntry entry)
    {
        var p = _upsert.Parameters;
        p["$p"].Value = entry.ParentId;
        p["$n"].Value = entry.Name;
        p["$f"].Value = entry.FileId;
        p["$ls"].Value = entry.LogicalSize;
        p["$sd"].Value = entry.SizeOnDisk;
        p["$mu"].Value = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        p["$hl"].Value = entry.IsHardLink ? 1 : 0;
        _upsert.ExecuteNonQuery();
    }

    public void Delete(long parentId, string name)
    {
        _delete.Parameters["$p"].Value = parentId;
        _delete.Parameters["$n"].Value = name;
        _delete.ExecuteNonQuery();
    }

    public bool DirectoryExists(long directoryId)
    {
        _dirExists.Parameters["$d"].Value = directoryId;
        return _dirExists.ExecuteScalar() is not null;
    }

    public void AddDirectory(long directoryId, long parentId, string name)
    {
        _addDir.Parameters["$d"].Value = directoryId;
        _addDir.Parameters["$p"].Value = parentId;
        _addDir.Parameters["$n"].Value = name;
        _addDir.ExecuteNonQuery();

        _addDirName.Parameters["$d"].Value = directoryId;
        _addDirName.Parameters["$p"].Value = parentId;
        _addDirName.Parameters["$n"].Value = name;
        _addDirName.Parameters["$t"].Value = DateTime.UtcNow.ToString("O");
        _addDirName.ExecuteNonQuery();
    }

    private static StoredEntry Read(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5) != 0);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _selectByKey.Dispose();
        _selectByFileId.Dispose();
        _upsert.Dispose();
        _delete.Dispose();
        _dirExists.Dispose();
        _addDir.Dispose();
        _addDirName.Dispose();
    }
}
