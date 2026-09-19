// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Security;

/// <summary>
/// The platform's current ticketing-integration configuration as returned by RustArchon.Api's
/// internal-only <c>GET /internal/ticketing-settings</c> endpoint - the only place this worker ever
/// sees a decrypted webhook signing secret. Mirrors the shape of <c>InternalController</c>'s response
/// DTO in RustArchon.Api; keep the two in sync if either changes.
/// </summary>
public record InternalTicketingSettings(string Provider, string WebhookUrl, string WebhookSecret);
