using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Strata.Storage;

/// <summary>
/// Owns the database file, its connection settings, and its migrations.
/// </summary>
/// <remarks>
/// Single process, single user, no concurrency beyond one writer and the read side of the API, so
/// this stays deliberately plain: no connection pool abstraction, no unit of work, no ORM.
/// </remarks>
public sealed class StrataDatabase
{
    private const string MigrationResourcePrefix = "Strata.Storage.Migrations.";

    public StrataDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    /// <summary>Default location: %LOCALAPPDATA%\Strata\strata.db.</summary>
    public static string DefaultPath => Path.Combine(DefaultDirectory, "strata.db");

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Strata");

    public SqliteConnection Open()
    {
        var directory = Path.GetDirectoryName(DatabasePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());

        connection.Open();
        ApplyPragmas(connection);

        return connection;
    }

    /// <summary>
    /// Connection settings tuned for a write-heavy baseline scan followed by mostly-read use.
    /// </summary>
    /// <remarks>
    /// WAL so a scan writing does not block the API reading. synchronous=NORMAL because losing the
    /// last few seconds of a scan on a power cut costs a rescan, not data anyone typed — the
    /// durability of a full fsync per commit is not worth its cost here.
    /// </remarks>
    private static void ApplyPragmas(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA synchronous = NORMAL;");
        Execute(connection, "PRAGMA temp_store = MEMORY;");
        Execute(connection, "PRAGMA cache_size = -65536;"); // 64 MB, negative means kibibytes
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "PRAGMA busy_timeout = 5000;");
    }

    /// <summary>
    /// Brings the schema up to date, creating the database if it does not exist.
    /// </summary>
    /// <remarks>
    /// Migrations are embedded SQL named NNN_description.sql and applied in numeric order inside a
    /// transaction, with the applied version recorded. An applied migration is never edited.
    /// </remarks>
    public void Migrate()
    {
        using var connection = Open();

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version     INTEGER PRIMARY KEY,
                name        TEXT NOT NULL,
                applied_utc TEXT NOT NULL
            );
            """);

        var applied = AppliedVersions(connection);

        foreach (var migration in DiscoverMigrations())
        {
            if (applied.Contains(migration.Version))
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT INTO schema_migrations (version, name, applied_utc) VALUES ($v, $n, $t);";
                record.Parameters.AddWithValue("$v", migration.Version);
                record.Parameters.AddWithValue("$n", migration.Name);
                record.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private static HashSet<int> AppliedVersions(SqliteConnection connection)
    {
        var versions = new HashSet<int>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations;";

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static List<Migration> DiscoverMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var migrations = new List<Migration>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(MigrationResourcePrefix, StringComparison.Ordinal) ||
                !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resource[MigrationResourcePrefix.Length..];
            var separator = fileName.IndexOf('_', StringComparison.Ordinal);

            if (separator <= 0 || !int.TryParse(fileName[..separator], out var version))
            {
                throw new InvalidOperationException(
                    $"Migration '{fileName}' is not named NNN_description.sql.");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Could not read migration '{resource}'.");
            using var reader = new StreamReader(stream);

            migrations.Add(new Migration(version, fileName, reader.ReadToEnd()));
        }

        migrations.Sort(static (a, b) => a.Version.CompareTo(b.Version));
        return migrations;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed record Migration(int Version, string Name, string Sql);
}
