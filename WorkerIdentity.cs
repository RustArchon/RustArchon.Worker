// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker;

/// <summary>
/// This process's identity for as long as it runs. Minted once, in memory, at startup - never
/// persisted. If the process restarts, it simply registers as a "new" worker; whatever it previously
/// owned gets reassigned once its heartbeats go stale (see <c>ServerClaimSweepService</c> in
/// RustArchon.Api). Registered as a singleton so every consumer and connection actor shares the same
/// value.
/// </summary>
public class WorkerIdentity
{
    public Guid Id { get; } = Guid.NewGuid();
}
