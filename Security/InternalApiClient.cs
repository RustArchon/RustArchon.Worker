// Copyright ©2026 Scott Blomfield

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RustArchon.Worker.Security;

/// <summary>
/// <see cref="IInternalApiClient"/> implementation over a typed <see cref="HttpClient"/>. The
/// client's base address and <c>X-Internal-Api-Key</c> header are configured once, in
/// <c>Program.cs</c>, via <see cref="Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions.AddHttpClient{TClient, TImplementation}(IServiceCollection, Action{HttpClient})"/>.
/// </summary>
public class InternalApiClient(HttpClient httpClient) : IInternalApiClient
{
    // ASP.NET Core's default JSON output is camelCase; System.Text.Json's default deserialization
    // options are case-sensitive and would otherwise silently fail to populate every property.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<InternalRustServerInfo?> GetServerAsync(Guid serverId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"internal/rust-servers/{serverId}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<InternalRustServerInfo>(JsonOptions, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InternalEmailSettings> GetEmailSettingsAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("internal/email-settings", cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<InternalEmailSettings>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("internal/email-settings returned an empty body.");
    }
}
