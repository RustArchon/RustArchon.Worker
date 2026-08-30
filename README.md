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
```
