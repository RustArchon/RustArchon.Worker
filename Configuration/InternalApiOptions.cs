// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Configuration;

/// <summary>
/// Settings for calling RustArchon.Api's internal (non-JWT) endpoints, bound from the <c>Api</c>
/// configuration section.
/// </summary>
/// <remarks>
/// The shared secret itself (sent as the <c>X-Internal-Api-Key</c> header) deliberately isn't a
/// property here - it's read directly from the flat <c>RUSTARCHON_INTERNAL_API_KEY</c> key in
/// <c>Program.cs</c>, the exact same name <c>RustArchon.Api</c> and the Blazor web app also read with
/// no rename in between. <c>BaseUrl</c> stays here since it isn't shared with anything else and isn't
/// secret - see <c>Program.cs</c>.
/// </remarks>
public class InternalApiOptions
{
    /// <summary>The base URL of RustArchon.Api, e.g. <c>http://rustarchon-api:8080</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;
}
