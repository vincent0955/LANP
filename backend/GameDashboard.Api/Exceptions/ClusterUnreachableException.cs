namespace GameDashboard.Api.Exceptions;

/// <summary>
/// Thrown when an operation cannot proceed because the Docker engine is
/// unreachable. Mapped to HTTP 503 by <see cref="Middleware.ExceptionHandlingMiddleware"/>
/// rather than a generic 500/409, since this reflects a temporary dependency outage,
/// not a client error. (The name predates the Docker migration — it kept the
/// same 503 semantics, so renaming it wasn't worth the churn.)
/// </summary>
public sealed class ClusterUnreachableException : Exception
{
    public ClusterUnreachableException(string message) : base(message) { }
}
