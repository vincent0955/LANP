using System.Text;

namespace GameDashboard.Api.Services.Runtime;

/// <summary>
/// Merge-edits the per-user %USERPROFILE%\.wslconfig to enable mirrored
/// networking (docs/docker-migration.md → Inbound networking). Pure text
/// transformation so it's unit-testable; the caller does the file I/O.
///
/// Mirrored networking is required for LAN/internet players: it binds
/// published game ports on the host's real interfaces, TCP AND UDP. The NAT
/// fallback's portproxy is TCP-only, which breaks Source-engine games.
///
/// Deliberately NOT handled here: keeping the runtime VM alive. That is done
/// by a Windows-side keep-alive session (RuntimeSetupService), because WSL's
/// vmIdleTimeout setting was verified empirically (2026-07-17) to not prevent
/// the idle shutdown — the VM died after the idle window with both -1 and
/// int.MaxValue configured.
///
/// The merge never clobbers unrelated settings: other sections and other
/// [wsl2] keys are preserved verbatim; only the targeted key is set/updated.
/// </summary>
public static class WslConfigEditor
{
    private const string SectionHeader = "[wsl2]";
    private const string NetworkingModeKey = "networkingMode";
    private const string MirroredValue = "mirrored";

    /// <summary>True when the given .wslconfig content already enables mirrored networking.</summary>
    public static bool HasMirroredNetworking(string? content) =>
        HasKeyValue(content, NetworkingModeKey, MirroredValue);

    /// <summary>
    /// Returns the content with networkingMode=mirrored set under [wsl2],
    /// creating the section when absent. Idempotent.
    /// </summary>
    public static string EnsureMirroredNetworking(string? content) =>
        EnsureKeyValue(content, NetworkingModeKey, MirroredValue);

    private static bool HasKeyValue(string? content, string key, string value)
    {
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        var inWsl2 = false;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                inWsl2 = line.Equals(SectionHeader, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inWsl2 && TryParseAssignment(line, out var foundKey, out var foundValue) &&
                foundKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return foundValue.Equals(value, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static string EnsureKeyValue(string? content, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return $"{SectionHeader}{Environment.NewLine}{key}={value}{Environment.NewLine}";
        }

        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();

        var wsl2Start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Equals(SectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                wsl2Start = i;
                continue;
            }

            if (wsl2Start >= 0 && line.StartsWith('['))
            {
                break; // end of the [wsl2] section
            }

            if (wsl2Start >= 0 && TryParseAssignment(line, out var foundKey, out _) &&
                foundKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}"; // update in place, keep everything else
                return Join(lines);
            }
        }

        if (wsl2Start >= 0)
        {
            lines.Insert(wsl2Start + 1, $"{key}={value}");
            return Join(lines);
        }

        // No [wsl2] section at all: append one, preserving the original text.
        var builder = new StringBuilder(Join(lines).TrimEnd('\n', '\r'));
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine(SectionHeader);
        builder.AppendLine($"{key}={value}");
        return builder.ToString();
    }

    private static bool TryParseAssignment(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        if (line.StartsWith('#') || line.StartsWith(';'))
        {
            return false; // comment
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        key = line[..separator].Trim();
        value = line[(separator + 1)..].Trim();
        return key.Length > 0;
    }

    private static string Join(IEnumerable<string> lines) =>
        string.Join(Environment.NewLine, lines);
}
