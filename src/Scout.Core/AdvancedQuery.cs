using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Scout.Core;

public sealed record AdvancedQuery(string Text, string? Pattern = null, bool Regex = false, string? Folder = null, string[]? Extensions = null, long? MinimumSize = null, long? MaximumSize = null, TimeWindow? ModifiedWindow = null, string? Error = null)
{
    private static readonly Regex Operator = new(@"(?<![\p{L}\p{N}_])(?<key>grep|regex|in|ext|size|mtime):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static AdvancedQuery Parse(string input, DateTimeOffset now)
    {
        string? pattern = null, folder = null, error = null; bool regex = false; string[]? extensions = null; long? min = null, max = null; TimeWindow? modified = null;
        var plain = new StringBuilder(); var cursor = 0;
        foreach (Match match in Operator.Matches(input))
        {
            if (match.Index < cursor) continue;
            plain.Append(input[cursor..match.Index]); var key = match.Groups["key"].Value.ToLowerInvariant(); var start = match.Index + match.Length; var end = start; string value;
            if (start < input.Length && input[start] == '"')
            {
                end = start + 1; var valueBuilder = new StringBuilder(); var closed = false;
                while (end < input.Length)
                {
                    var c = input[end++];
                    if (c == '"' ) { closed = true; break; }
                    if (key is "grep" or "regex" && c == '\\' && end < input.Length && input[end] == '"') { valueBuilder.Append('"'); end++; }
                    else valueBuilder.Append(c);
                }
                value = valueBuilder.ToString(); if (!closed) error = "Close the quoted query value.";
            }
            else { while (end < input.Length && !char.IsWhiteSpace(input[end])) end++; value = input[start..end]; }
            cursor = end; plain.Append(' ');
            if (value.Length == 0) { error = $"Supply a value for {key}:."; continue; }
            try
            {
                switch (key)
                {
                    case "grep": case "regex":
                        if (pattern != null) throw new FormatException("Use one content pattern per query.");
                        if (value.Length > 512) throw new FormatException("Content patterns are limited to 512 characters.");
                        pattern = value; regex = key == "regex"; break;
                    case "in": folder = value; break;
                    case "ext": extensions = value.Split(',').Select(e => "." + e.Trim().TrimStart('.').ToLowerInvariant()).ToArray(); break;
                    case "size":
                        var range = Range(value); var size = Size(range.Value);
                        switch (range.Op) { case ">": min = checked(size + 1); break; case ">=": min = size; break; case "<": max = checked(size - 1); break; case "<=": max = size; break; default: min = max = size; break; }
                        break;
                    case "mtime":
                        var age = Range(value); var duration = Age(age.Value); var cutoff = now - duration;
                        modified = age.Op is "<" or "<=" ? new(cutoff, now.AddSeconds(2), value) : age.Op is ">" or ">=" ? new(DateTimeOffset.MinValue, cutoff, value) : throw new FormatException("Use mtime:<7d or mtime:>7d."); break;
                }
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException) { error = e.Message; }
        }
        plain.Append(input[cursor..]); return new(plain.ToString().Trim(), pattern, regex, folder, extensions, min, max, modified, error);
    }
    private static (string Op, string Value) Range(string value)
    {
        foreach (var op in new[] { ">=", "<=", ">", "<", "=" }) if (value.StartsWith(op, StringComparison.Ordinal)) return (op, value[op.Length..]); return ("=", value);
    }
    private static long Size(string value)
    {
        var m = System.Text.RegularExpressions.Regex.Match(value, @"^(\d+(?:\.\d+)?)(b|kb|mb|gb|tb)?$", RegexOptions.IgnoreCase);
        if (!m.Success) throw new FormatException("Use a file size such as size:>5mb.");
        var factor = m.Groups[2].Value.ToLowerInvariant() switch { "kb" => 1024L, "mb" => 1024L * 1024, "gb" => 1024L * 1024 * 1024, "tb" => 1024L * 1024 * 1024 * 1024, _ => 1 };
        return checked((long)(decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * factor));
    }
    private static TimeSpan Age(string value)
    {
        var m = System.Text.RegularExpressions.Regex.Match(value, @"^(\d+(?:\.\d+)?)(m|h|d|w)$", RegexOptions.IgnoreCase);
        if (!m.Success) throw new FormatException("Use an age such as mtime:<7d.");
        var minutes = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * (m.Groups[2].Value.ToLowerInvariant() switch { "h" => 60, "d" => 1440, "w" => 10080, _ => 1 });
        return TimeSpan.FromMinutes(minutes);
    }
}
