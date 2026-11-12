using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DuetEmulation.Emulator;

/// <summary>
/// How values are written in Renode monitor commands, and read back from what they print
/// </summary>
public static partial class MonitorSyntax
{
    /// <summary>A string argument, quoted</summary>
    public static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// <summary>A path argument</summary>
    /// <exception cref="ArgumentException">The path has a character the monitor cannot take in a path</exception>
    public static string PathArgument(string path)
    {
        // A path token ends at whitespace and has no quoting of its own
        if (path.AsSpan().IndexOfAny(" \t\"'`;") >= 0)
        {
            throw new ArgumentException($"Renode cannot load a path with spaces or quotes in it: {path}", nameof(path));
        }
        return "@" + path;
    }

    /// <summary>A number in hexadecimal</summary>
    public static string Hex(ulong value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>A floating point argument, quoted, as Renode takes time intervals</summary>
    public static string Seconds(TimeSpan time) =>
        Quote(time.TotalSeconds.ToString("0.0#########", CultureInfo.InvariantCulture));

    /// <summary>A floating point argument</summary>
    public static string Decimal(double value) => value.ToString("0.0#########", CultureInfo.InvariantCulture);

    /// <summary>A boolean argument</summary>
    public static string Bool(bool value) => value ? "true" : "false";

    /// <summary>The last number a command printed, in decimal or hexadecimal</summary>
    public static bool TryParseNumber(string output, out ulong value)
    {
        MatchCollection numbers = NumberPattern().Matches(output);
        value = 0;
        if (numbers.Count == 0)
        {
            return false;
        }
        string last = numbers[^1].Value;
        return last.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(last.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : ulong.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The value a command that returns a boolean printed</summary>
    public static bool ParseBool(string output) => output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"0x[0-9a-fA-F]+|\b\d+\b")]
    private static partial Regex NumberPattern();
}
