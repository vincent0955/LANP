using System.Text.RegularExpressions;

namespace GameDashboard.Api.Logging;

/// <summary>
/// Redacts known secret values/keys before they reach logs.
/// See requirements.md → Req 14 (secrets must never appear in logs).
///
/// This is a defensive helper used when logging config maps, env vars, or RCON-related
/// data. It is deliberately conservative: any key whose name contains a sensitive token
/// has its value masked.
/// </summary>
public static partial class SecretRedactor
{
    private const string Mask = "***REDACTED***";

    /// <summary>Key-name fragments that indicate a sensitive value (case-insensitive).</summary>
    private static readonly string[] SensitiveKeyFragments =
    {
        "TOKEN", "PASSWORD", "PASSWD", "RCONPW", "SECRET", "APIKEY", "API_KEY", "PW"
    };

    /// <summary>
    /// Returns a copy of the dictionary with sensitive values masked.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values)
    {
        var result = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            result[key] = IsSensitiveKey(key) ? Mask : value;
        }
        return result;
    }

    /// <summary>
    /// True if the key name looks sensitive.
    /// </summary>
    public static bool IsSensitiveKey(string key)
    {
        foreach (var fragment in SensitiveKeyFragments)
        {
            if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Masks "key=value" style secrets inside a free-form string (e.g. a command line
    /// or log line). Matches assignments where the key looks sensitive.
    /// </summary>
    public static string RedactString(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        return SecretAssignmentRegex().Replace(input, match =>
        {
            var key = match.Groups["key"].Value;
            return IsSensitiveKey(key) ? $"{key}{match.Groups["sep"].Value}{Mask}" : match.Value;
        });
    }

    // Matches KEY=value or KEY: value (value = non-space run), capturing key and separator.
    [GeneratedRegex(@"(?<key>[A-Za-z_][A-Za-z0-9_]*)(?<sep>\s*[:=]\s*)(?<val>\S+)")]
    private static partial Regex SecretAssignmentRegex();
}
