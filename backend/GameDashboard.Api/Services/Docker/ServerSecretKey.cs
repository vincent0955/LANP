namespace GameDashboard.Api.Services.Docker;

/// <summary>
/// Namespaces secret-store keys by server so each deployed server keeps its own
/// secret values (its own Steam GSLT token, its own RCON password) instead of a
/// single value shared by every server of that game. The underlying
/// <see cref="ISecretsStore"/> stays a flat key/value map; the per-server scope
/// is a naming convention layered on top of it.
///
/// The scoped key is <c>{serverName}/{storeKey}</c>. This is unambiguous because
/// server names are DNS-1123 labels (lowercase alphanumeric or '-', see
/// <see cref="ServerNameValidator"/>) and store keys are environment-variable
/// names — neither can contain a '/'.
/// </summary>
public static class ServerSecretKey
{
    public static string Scope(string serverName, string storeKey) => $"{serverName}/{storeKey}";

    /// <summary>Prefix that all of a server's scoped keys share, for bulk cleanup on delete.</summary>
    public static string Prefix(string serverName) => $"{serverName}/";
}
