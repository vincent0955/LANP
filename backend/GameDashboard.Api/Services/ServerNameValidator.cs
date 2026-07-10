using System.Text.RegularExpressions;

namespace GameDashboard.Api.Services;

/// <summary>
/// Validates Kubernetes object names against the DNS-1123 label rules that
/// Deployments, Services, PVCs, and ConfigMaps all require for their metadata.name.
/// See requirements.md → Req 14.5 (validate/sanitize user input before hitting the
/// Kubernetes API).
/// </summary>
public static partial class ServerNameValidator
{
    private const int MaxLength = 63;

    /// <summary>
    /// True if <paramref name="name"/> is a valid DNS-1123 label: lowercase
    /// alphanumeric characters or '-', starting and ending with an alphanumeric
    /// character, 1-63 characters long.
    /// </summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength)
        {
            return false;
        }

        return Dns1123LabelRegex().IsMatch(name);
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> with a descriptive message if
    /// <paramref name="name"/> is not a valid DNS-1123 label.
    /// </summary>
    public static void EnsureValid(string? name)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException(
                $"Server name '{name}' is invalid. Names must be 1-{MaxLength} characters, " +
                "lowercase alphanumeric or '-', and must start/end with an alphanumeric character " +
                "(DNS-1123 label rules).",
                nameof(name));
        }
    }

    [GeneratedRegex(@"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex Dns1123LabelRegex();
}
