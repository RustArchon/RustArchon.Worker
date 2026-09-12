// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Security;

/// <summary>
/// The platform's current email delivery configuration as returned by RustArchon.Api's internal-only
/// <c>GET /internal/email-settings</c> endpoint - the only place this worker ever sees a decrypted SMTP
/// password or provider API key. Mirrors the shape of <c>InternalController</c>'s response DTO in
/// RustArchon.Api; keep the two in sync if either changes.
/// </summary>
public record InternalEmailSettings(
    string ServiceProvider,
    string SmtpHost,
    int SmtpPort,
    bool SmtpEnableSsl,
    string SmtpUsername,
    string SmtpPassword,
    string ApiKey,
    string DefaultFromAddress,
    string DefaultFromName);
