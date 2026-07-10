namespace GameDashboard.Api.Exceptions;

/// <summary>
/// Thrown when a Kubernetes operation cannot proceed because the cluster is
/// unreachable. Mapped to HTTP 503 by <see cref="Middleware.ExceptionHandlingMiddleware"/>
/// rather than a generic 500/409, since this reflects a temporary dependency outage,
/// not a client error.
/// </summary>
public sealed class ClusterUnreachableException : Exception
{
    public ClusterUnreachableException(string message) : base(message) { }
}
