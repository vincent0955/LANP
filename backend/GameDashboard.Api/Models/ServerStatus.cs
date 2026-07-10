namespace GameDashboard.Api.Models;

/// <summary>
/// Effective status of a deployed game server, derived from its Deployment replica
/// count and pod phase. See requirements.md → Req 2.
/// </summary>
public enum ServerStatus
{
    /// <summary>Replicas &gt; 0 and the pod is running and ready.</summary>
    Running,

    /// <summary>Replicas == 0 (scaled down); resources preserved.</summary>
    Stopped,

    /// <summary>Replicas &gt; 0 but the pod has not reached Running/Ready yet.</summary>
    Pending,

    /// <summary>Replicas &gt; 0 but the pod is crash-looping, failed, or otherwise unhealthy.</summary>
    Error,

    /// <summary>Status could not be determined (e.g. no pod found for a scaled-up deployment).</summary>
    Unknown
}
