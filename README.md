# RustArchon.Worker

The persistent-connection host: one [RustArchon.Rcon](https://github.com/RustArchon/RustArchon.Rcon)
client per Rust server this instance owns, publishing captured console/chat frames, connection
status, and heartbeats as [RustArchon.Messaging](https://github.com/RustArchon/RustArchon.Messaging)
contracts over RabbitMQ for [RustArchon.Api](https://github.com/RustArchon/RustArchon.Api) to
consume. Has no database connection at all. Horizontally scalable - multiple instances compete for
work off a shared queue rather than needing a coordinator; see the remarks in
`Connections/ConnectionSupervisor.cs`.

Part of the [RustArchon](https://github.com/RustArchon/RustArchon) system - see that repo for the
full architecture and how to run the whole stack locally or via Docker Compose.

## Key files

- `Connections/ConnectionSupervisor.cs` - owns the set of `ServerConnectionActor`s this instance is
  currently responsible for; the queue-based ownership/claim mechanism lives here.
- `Connections/ServerConnectionActor.cs` - one per owned server: the actual `RustArchon.Rcon` client,
  reconnect-with-backoff loop, and the per-connection heartbeat/status publishing.
- `Messaging/ConnectToServerConsumer.cs`, `ServerLifecycleConsumer.cs`, `SendRconCommandConsumer.cs` -
  the MassTransit consumers that drive the above: claim a server, react to it being
  updated/disabled/deleted, and dispatch a command to whichever instance actually owns the
  connection.
- `Messaging/EmailRequestedConsumer.cs`, `Email/{IEmailDeliveryProvider,NoOpEmailDeliveryProvider}.cs` -
  the queued-email pipeline (see the umbrella README's "Before deploying this anywhere real" -
  `NoOpEmailDeliveryProvider` needs replacing with a real provider before production).
- `Security/InternalApiClient.cs` - calls RustArchon.Api's internal endpoints (credential handoff,
  server info) using the shared internal API key, not a user JWT.

## License

AGPL-3.0-or-later - see [`LICENSE`](LICENSE). See [`NOTICE.md`](NOTICE.md) for how this project
relates to [JumpStart](https://github.com/cyberknet/JumpStart) elsewhere in the RustArchon system
(this project itself has no JumpStart dependency).

## Building standalone

**This repo cannot be built on its own** - it references `RustArchon.Messaging` and
`RustArchon.Rcon` by relative path, which only resolve inside the
[umbrella repo's](https://github.com/RustArchon/RustArchon) submodule layout. Clone that instead:

```bash
git clone --recurse-submodules https://github.com/RustArchon/RustArchon.git
cd RustArchon/RustArchon.Worker
dotnet run
```

Needs RabbitMQ and a running `RustArchon.Api` instance reachable at its configured `Api:BaseUrl` to
do anything useful - see the umbrella README's "Running locally".
