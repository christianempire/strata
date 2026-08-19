using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Strata.Storage;

/// <summary>Reads and writes the <c>settings</c> key/value table.</summary>
/// <remarks>
/// Anything missing from the table falls back to the default on <see cref="StrataSettings"/>, so a
/// database written by an older build keeps working and a new setting needs no migration. The
/// corollary is that a value the table does not recognise is ignored rather than being an error.
/// </remarks>
public static class SettingsRepository
{
    public static StrataSettings Load(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var stored = ReadAll(connection);
        var defaults = new StrataSettings();

        return new StrataSettings
        {
            IntervalSeconds = Int32(stored, "intervalSeconds", defaults.IntervalSeconds),
            CpuThresholdPercent = Double(stored, "cpuThresholdPercent", defaults.CpuThresholdPercent),
            MaxRecordsPerTick = Int32(stored, "maxRecordsPerTick", defaults.MaxRecordsPerTick),
            EventFloorBytes = Int64(stored, "eventFloorBytes", defaults.EventFloorBytes),
            SnapshotMaxDepth = Int32(stored, "snapshotMaxDepth", defaults.SnapshotMaxDepth),
            SnapshotSizeFloorBytes = Int64(stored, "snapshotSizeFloorBytes", defaults.SnapshotSizeFloorBytes),
            HistoryBandBytes = Int64(stored, "historyBandBytes", defaults.HistoryBandBytes),
            ScanWorkers = NullableInt32(stored, "scanWorkers"),
            BackgroundIo = Bool(stored, "backgroundIo", defaults.BackgroundIo),
            DetectBorderlessFullscreen =
                Bool(stored, "detectBorderlessFullscreen", defaults.DetectBorderlessFullscreen),
            IncludeStrataDatabase = Bool(stored, "includeStrataDatabase", defaults.IncludeStrataDatabase),
            ExcludedEventPaths = Strings(stored, "excludedEventPaths"),
            ExcludedDirectoryNames = Strings(stored, "excludedDirectoryNames"),
            Paused = Bool(stored, "paused", defaults.Paused),
        };
    }

    /// <summary>Replaces the stored configuration. Rejects a configuration that would not work.</summary>
    public static void Save(SqliteConnection connection, StrataSettings settings)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);

        var errors = settings.Validate();

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["intervalSeconds"] = Text(settings.IntervalSeconds),
            ["cpuThresholdPercent"] = Text(settings.CpuThresholdPercent),
            ["maxRecordsPerTick"] = Text(settings.MaxRecordsPerTick),
            ["eventFloorBytes"] = Text(settings.EventFloorBytes),
            ["snapshotMaxDepth"] = Text(settings.SnapshotMaxDepth),
            ["snapshotSizeFloorBytes"] = Text(settings.SnapshotSizeFloorBytes),
            ["historyBandBytes"] = Text(settings.HistoryBandBytes),
            ["scanWorkers"] = settings.ScanWorkers is null ? "" : Text(settings.ScanWorkers.Value),
            ["backgroundIo"] = Text(settings.BackgroundIo),
            ["detectBorderlessFullscreen"] = Text(settings.DetectBorderlessFullscreen),
            ["includeStrataDatabase"] = Text(settings.IncludeStrataDatabase),
            ["excludedEventPaths"] = JsonSerializer.Serialize(settings.ExcludedEventPaths),
            ["excludedDirectoryNames"] = JsonSerializer.Serialize(settings.ExcludedDirectoryNames),
            ["paused"] = Text(settings.Paused),
        };

        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO settings (key, value, updated_utc) VALUES ($k, $v, $t)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc;
            """;

        command.Parameters.Add("$k", SqliteType.Text);
        command.Parameters.Add("$v", SqliteType.Text);
        command.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        command.Prepare();

        foreach (var (key, value) in values)
        {
            command.Parameters["$k"].Value = key;
            command.Parameters["$v"].Value = value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static Dictionary<string, string> ReadAll(SqliteConnection connection)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings;";

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(bool value) => value ? "1" : "0";

    private static int Int32(Dictionary<string, string> stored, string key, int fallback)
        => stored.TryGetValue(key, out var text)
           && int.TryParse(text, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static int? NullableInt32(Dictionary<string, string> stored, string key)
        => stored.TryGetValue(key, out var text)
           && int.TryParse(text, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static long Int64(Dictionary<string, string> stored, string key, long fallback)
        => stored.TryGetValue(key, out var text)
           && long.TryParse(text, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static double Double(Dictionary<string, string> stored, string key, double fallback)
        => stored.TryGetValue(key, out var text)
           && double.TryParse(text, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool Bool(Dictionary<string, string> stored, string key, bool fallback)
        => stored.TryGetValue(key, out var text) ? text is "1" or "true" or "True" : fallback;

    private static string[] Strings(Dictionary<string, string> stored, string key)
    {
        if (!stored.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(text) ?? [];
        }
        catch (JsonException)
        {
            // Hand-edited into something unparseable. Falling back to nothing excluded is visible
            // in the UI and recoverable; throwing here would stop the scanner from starting.
            return [];
        }
    }
}
