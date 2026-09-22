// Copyright ©2026 Scott Blomfield

using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RustArchon.Worker;
using RustArchon.Worker.Configuration;
using RustArchon.Worker.Connections;
using RustArchon.Worker.Email;
using RustArchon.Worker.Messaging;
using RustArchon.Worker.Security;
using RustArchon.Worker.Ticketing;

var builder = Host.CreateApplicationBuilder(args);

// ============================================
// 1. CONFIGURATION
// ============================================
builder.Services.Configure<InternalApiOptions>(builder.Configuration.GetSection("Api"));
builder.Services.Configure<ReconnectOptions>(builder.Configuration.GetSection("Reconnect"));

// Username/Password come from RABBITMQ_DEFAULT_USER/PASS directly - the RabbitMQ container's own
// required env var names, not a RustArchon-specific rename. See RabbitMqOptions's remarks.
var rabbitMqOptions = new RabbitMqOptions
{
    Host = builder.Configuration["RabbitMq:Host"] ?? "localhost",
    VirtualHost = builder.Configuration["RabbitMq:VirtualHost"] ?? "/",
    Username = builder.Configuration["RABBITMQ_DEFAULT_USER"] ?? "guest",
    Password = builder.Configuration["RABBITMQ_DEFAULT_PASS"] ?? "guest"
};
var internalApiOptions = builder.Configuration.GetSection("Api").Get<InternalApiOptions>()
    ?? throw new InvalidOperationException("Api configuration section is missing.");

// Read directly from the flat RUSTARCHON_INTERNAL_API_KEY key - the exact same name RustArchon.Api
// and the Blazor web app also read, with no Section:Key rename in between. See
// InternalApiOptions's remarks for why this isn't a property on that class.
var internalApiKey = builder.Configuration["RUSTARCHON_INTERNAL_API_KEY"]
    ?? throw new InvalidOperationException("RUSTARCHON_INTERNAL_API_KEY configuration is missing.");

// ============================================
// 2. WORKER IDENTITY + CONNECTION STATE
// ============================================
// A fresh identity every process start - never persisted. See WorkerIdentity's remarks.
builder.Services.AddSingleton<WorkerIdentity>();
builder.Services.AddSingleton<IConnectionSupervisor, ConnectionSupervisor>();

// ============================================
// 2b. EMAIL DELIVERY
// ============================================
// No fixed IEmailDeliveryProvider registration - which provider (SMTP, SendGrid, or none at all)
// applies is decided per-send, from settings fetched fresh off RustArchon.Api, since an admin can
// change the platform's email settings at any time and this process has no way to be notified of
// that. See IEmailDeliveryProviderFactory's remarks.
//
// RUSTARCHON_SUPPRESS_EMAIL_DELIVERY is a separate, deployment-level override read once here at
// startup (unset/false unless a deployment's .env sets it) - lets a test/staging instance keep a real
// provider configured in the platform settings (to exercise that UI end to end) while guaranteeing
// nothing this process sends ever actually leaves it. See EmailDeliveryOptions's remarks for why this
// exists.
var suppressEmailDelivery = builder.Configuration.GetValue<bool>("RUSTARCHON_SUPPRESS_EMAIL_DELIVERY");
builder.Services.AddSingleton(new EmailDeliveryOptions(suppressEmailDelivery));
builder.Services.AddSingleton<IEmailDeliveryProviderFactory, EmailDeliveryProviderFactory>();

// ============================================
// 2c. TICKETING INTEGRATION
// ============================================
// Same reasoning as email delivery just above - which provider (Internal/Webhook) applies is decided
// per-event, from settings fetched fresh off RustArchon.Api, since an admin can flip this at any time.
builder.Services.AddSingleton<ITicketingIntegrationProviderFactory, TicketingIntegrationProviderFactory>();

// ============================================
// 3. INTERNAL API CLIENT
// ============================================
// Never a user JWT - a completely separate, non-tenant-scoped shared secret. This is the only place
// this process ever sees a decrypted RCON password, and only for as long as a connection actor holds
// it in memory.
builder.Services.AddHttpClient<IInternalApiClient, InternalApiClient>(client =>
{
    client.BaseAddress = new Uri(internalApiOptions.BaseUrl);
    client.DefaultRequestHeaders.Add("X-Internal-Api-Key", internalApiKey);
});

// ============================================
// 4. MASSTRANSIT / RABBITMQ
// ============================================
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<ConnectToServerConsumer>();
    x.AddConsumer<ServerLifecycleConsumer>();
    x.AddConsumer<SendRconCommandConsumer>();
    x.AddConsumer<PollServerNowConsumer>();
    x.AddConsumer<EmailRequestedConsumer>();
    x.AddConsumer<SendTestEmailConsumer>();
    x.AddConsumer<TicketEventConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqOptions.Host, rabbitMqOptions.VirtualHost, h =>
        {
            h.Username(rabbitMqOptions.Username);
            h.Password(rabbitMqOptions.Password);
        });

        var workerId = context.GetRequiredService<WorkerIdentity>().Id.ToString("N");

        // Competing consumer: one shared, durable, stably-named queue - every worker instance binds
        // the SAME queue, so RabbitMQ's normal fair dispatch hands each message to exactly one live
        // instance. This is the entire mechanism behind letting N worker instances share ownership of
        // the server fleet without coordinating with each other directly. See the plan's §1/§2.
        cfg.ReceiveEndpoint("rustarchon-worker-connect-to-server", e =>
        {
            e.ConfigureConsumer<ConnectToServerConsumer>(context);
        });

        // Fanout: each instance binds its own uniquely-named, non-durable, auto-delete queue, so
        // every live instance gets its own copy of these two message types. Getting this backwards
        // (sharing one queue here, the way ConnectToServer does above) would silently break ownership
        // takeover and command dispatch - see the plan's §1.
        cfg.ReceiveEndpoint($"rustarchon-worker-lifecycle-{workerId}", e =>
        {
            e.Durable = false;
            e.AutoDelete = true;
            e.ConfigureConsumer<ServerLifecycleConsumer>(context);
        });

        cfg.ReceiveEndpoint($"rustarchon-worker-command-{workerId}", e =>
        {
            e.Durable = false;
            e.AutoDelete = true;
            e.ConfigureConsumer<SendRconCommandConsumer>(context);
        });

        cfg.ReceiveEndpoint($"rustarchon-worker-poll-{workerId}", e =>
        {
            e.Durable = false;
            e.AutoDelete = true;
            e.ConfigureConsumer<PollServerNowConsumer>(context);
        });

        // Competing consumer like ConnectToServer above - one shared, durable queue (explicit here,
        // even though Durable is already the default, since durability is the entire point of this
        // one: it's what "guaranteed eventual delivery" actually rests on, along with the RabbitMQ
        // data volume in docker-compose.yml - a durable queue with nowhere durable to persist to
        // still loses everything if the container is recreated). Retries a failed send 5 times with
        // growing backoff before MassTransit routes the message to its default error queue rather than
        // losing it - check the "rustarchon-worker-email" queue (and its paired "_error" queue) in the
        // RabbitMQ management UI to see this directly.
        cfg.ReceiveEndpoint("rustarchon-worker-email", e =>
        {
            e.Durable = true;
            e.UseMessageRetry(r => r.Intervals(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(5)));
            e.ConfigureConsumer<EmailRequestedConsumer>(context);
        });

        // Competing consumer, same reasoning as the email queue above - but no retry policy: this is
        // an interactive admin action with someone waiting on the response, not a queued send that
        // should keep trying on its own schedule after the caller has already stopped waiting.
        cfg.ReceiveEndpoint("rustarchon-worker-test-email", e =>
        {
            e.ConfigureConsumer<SendTestEmailConsumer>(context);
        });

        // Competing consumer, durable queue, same retry policy as the email queue above - a webhook
        // delivery to an external system is exactly the same "should keep trying with backoff, never
        // silently drop it" concern as sending an email.
        cfg.ReceiveEndpoint("rustarchon-worker-ticket-events", e =>
        {
            e.Durable = true;
            e.UseMessageRetry(r => r.Intervals(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(5)));
            e.ConfigureConsumer<TicketEventConsumer>(context);
        });
    });
});

var app = builder.Build();
app.Run();
