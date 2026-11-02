# Progress

Short notes per stage or week: what was done, what was learned about the system, problems.

## Stage 0 — Foundation (2026-10-09 … 2026-10-15)

**Done**
- Repository skeleton: .NET 10 SDK pinned, Central Package Management, analyzers with warnings as errors, `.slnx`; domain model `VehiclePosition` with invariants and 19 tests.
- GitHub process: CI (build, test, format) on every PR, ruleset on `main` (PR only, required check, squash only), issue and PR templates, milestones per stage.
- Claude Code tooling: hooks (build/test after edits, format before commit), skills, reviewer and test-writer subagents.
- Data source terms: licences, attribution and mandatory request rules for HSL, Entur, MBTA, Digitraffic and Open-Meteo ([data-sources.md](data-sources.md)).
- Local infrastructure on .NET Aspire: PostgreSQL with TimescaleDB and PostGIS, Redis, Redpanda and Redpanda Console, with memory limits mirroring the production budget.
- ADRs: [0001](adr/0001-record-architecture-decisions.md) (record decisions), [0002](adr/0002-use-redpanda-instead-of-apache-kafka.md) (Redpanda instead of Apache Kafka).

**Learned**
- Persistent containers keep state that must match the AppHost configuration: auto-generated passwords changed on every run and broke the Postgres volume on the second start. Fix: explicit secret parameters and fixed ports; the check is now "two runs in a row", not one.
- `timescale/timescaledb-ha` stores data outside the default Postgres path, and init scripts run before application databases exist — extensions go into `template1`.
- MBTA terms are a revocable licence agreement rather than CC BY; the raw archive and replay (brief §13) cover that risk.
- Idle memory, measured locally: Postgres 88 MiB, Redis 10 MiB, Redpanda 277 MiB, Redpanda Console 45 MiB.

**Problems**
- The Aspire dashboard showed no resources until the ASP.NET Core development certificate was trusted (`dotnet dev-certs https --trust`).
- The collector server has only 10 GB of disk: raw data stays on it for about a day, so the daily transfer and a disk alert are required from day one of stage 1.

## Stage 1 — Data collection (2026-10-15 … 2026-11-02)

**Done**
- Ingestor (.NET 10, Native AOT) collecting ten feeds of three cities 24/7 on a 2 vCPU / 2 GB server since 2026-10-21: HSL positions over MQTT, Entur SIRI-VM/ET/SX, MBTA and HSL GTFS-RT, Open-Meteo weather, plus twice-daily GTFS schedule snapshots. Raw responses are kept as received in hourly Parquet + zstd files, copied to a local PC over SSH with SHA-256 checks ([ADR-0003](adr/0003-run-the-collector-as-native-aot-with-a-raw-parquet-archive.md)).
- Three adapters normalize JSON/MQTT push, incremental SIRI XML pull and GTFS-RT protobuf pull into one `VehiclePosition` and publish it to `positions.raw` ([ADR-0006](adr/0006-normalize-every-feed-into-one-vehicle-position-model.md)); contract tests on saved real responses.
- Own MQTT 3.1.1 client ([ADR-0004](adr/0004-use-a-minimal-mqtt-client-for-the-hsl-feed.md)), positions dropped under backpressure and rebuilt from the archive ([ADR-0005](adr/0005-drop-positions-under-backpressure-and-rebuild-them-from-the-raw-archive.md)), own restart backoff ([ADR-0007](adr/0007-retry-feed-sources-with-own-backoff-instead-of-polly.md)), per-feed metrics and a "feed is silent" health check.
- Replay mode (×1…×50 or as fast as possible) and archive tools (`--stats`, `--profile`, `--profile-positions`, `--repair`); the daily flow profile is in [benchmarks.md](benchmarks.md).

**Learned**
- The weekday peak is twice the evening sample used in the brief: HSL ~1,540 messages/s at 08:00 and 16:00. 22–32 % of HSL messages are byte-for-byte copies (trams send each message four times), so ~1,180 positions/s remain; Bergen and Boston add ~32 and ~43.
- Sources differ in more than format. MBTA TripUpdates carry no `delay` at all, so Boston's delay must come from the schedule. A fresh SIRI `requestorId` returns the whole day's journeys in several pages, and only VM marks the pages with `MoreData`. HSL trip start times run past 24:00 and shifted by an hour on the night clocks went back.
- A Parquet file is unreadable without its footer, so hourly files are written as `.tmp` and renamed on close; flushing row groups by time would not make an open file readable.
- One vCPU was not enough on a weekday (`cpu.pressure` full 41–61 %); the collector moved to 2 vCPU after measuring.
- `PublishAot` turns off EventSource, so `dotnet-counters` cannot see the process unless `EventSourceSupport=true` is set; `Confluent.Kafka` needs `TrimmerRootAssembly` under AOT.

**Problems**
- 102 out-of-memory restarts over 40 h (2026-10-19/20) went unnoticed until the health script learned to count restarts and memory.
- No collection on 2026-10-21 10:24–19:15 UTC (server move); no schedule snapshots before 20–21.10.
- A local `full` run shared the server's `ET-Client-Name` and both hit Entur's rate limit; local runs now use their own client name.
- The stage took 2.5 weeks instead of 1.5.
