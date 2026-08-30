// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Security;

/// <summary>
/// A server's connection details as returned by RustArchon.Api's internal-only
/// <c>GET /internal/rust-servers/{id}</c> endpoint - the only place this worker ever sees a decrypted
/// RCON password. Mirrors the shape of <c>InternalController</c>'s response DTO in RustArchon.Api;
/// keep the two in sync if either changes.
/// </summary>
public record InternalRustServerInfo(
    Guid Id,
    Guid TenantId,
    string Host,
    int Port,
    string RconPassword,
    Guid? AssignedWorkerId,
    DateTimeOffset? LastHeartbeatUtc);
