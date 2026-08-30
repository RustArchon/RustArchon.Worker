// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Configuration;

/// <summary>
/// Connection settings for the RabbitMQ broker. Mirrors
/// <c>RustArchon.Api.Infrastructure.RabbitMqOptions</c> - each project builds its own copy rather than
/// sharing a type across the Api/Worker boundary.
/// </summary>
/// <remarks>
/// Built manually in <c>Program.cs</c>, not bound from one section: <see cref="Username"/>/
/// <see cref="Password"/> are read directly from <c>RABBITMQ_DEFAULT_USER</c>/
/// <c>RABBITMQ_DEFAULT_PASS</c> - the RabbitMQ container's own required env var names (see
/// docker-compose.yml's <c>rabbitmq</c> service) - rather than a RustArchon-specific rename, since
/// those names are already fixed by the image and adding a parallel name would only be one more thing
/// to keep in sync for no benefit. <see cref="Host"/>/<see cref="VirtualHost"/> aren't secret and
/// aren't shared with the container's own config, so they still come from the "RabbitMq" section.
/// </remarks>
public class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
}
