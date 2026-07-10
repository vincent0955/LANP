namespace GameDashboard.Api.Exceptions;

/// <summary>
/// Thrown when attempting to deploy a server whose name already exists.
/// Mapped to HTTP 409 Conflict. See requirements.md → Req 3.5.
/// </summary>
public sealed class ServerAlreadyExistsException : Exception
{
    public ServerAlreadyExistsException(string name)
        : base($"A server named '{name}' already exists.") { }
}

/// <summary>
/// Thrown when a requested image tag / game is not known to the catalog.
/// Mapped to HTTP 400 Bad Request. See requirements.md → Req 3.4.
/// </summary>
public sealed class UnknownGameException : Exception
{
    public UnknownGameException(string imageTag)
        : base($"'{imageTag}' is not a known game. Deploy is currently limited to curated games.") { }
}

/// <summary>
/// Thrown when an operation targets a server that does not exist.
/// Mapped to HTTP 404 Not Found.
/// </summary>
public sealed class ServerNotFoundException : Exception
{
    public ServerNotFoundException(string name)
        : base($"Server '{name}' was not found.") { }
}

/// <summary>
/// Thrown when an explicit RCON command cannot be delivered (connection failed,
/// timed out, or authentication rejected). Mapped to HTTP 503.
/// See requirements.md → Req 10.5.
/// </summary>
public sealed class RconUnavailableException : Exception
{
    public RconUnavailableException(string serverName, string reason)
        : base($"RCON is unavailable for server '{serverName}': {reason}") { }
}
