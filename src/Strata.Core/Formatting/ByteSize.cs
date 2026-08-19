using System.Globalization;

namespace Strata.Core.Formatting;

/// <summary>Human-readable byte counts for CLI and log output.</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Formats a byte count using binary units: 1 KB is 1024 bytes.
    /// <para>
    /// Binary rather than decimal on purpose — this is a Windows tool, and matching what Explorer
    /// shows matters more than matching what a drive manufacturer prints on the box.
    /// </para>
    /// </summary>
    public static string Format(long bytes, int decimals = 1)
    {
        var negative = bytes < 0;
        var value = negative ? -(double)bytes : bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var rounded = Math.Round(value, unit == 0 ? 0 : decimals);
        var text = rounded.ToString(unit == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture);

        return (negative ? "-" : "") + text + " " + Units[unit];
    }

    /// <summary>Formats with an explicit sign, for deltas where direction is the point.</summary>
    public static string FormatDelta(long bytes, int decimals = 1)
        => (bytes > 0 ? "+" : "") + Format(bytes, decimals);
}
