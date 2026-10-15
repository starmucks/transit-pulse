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
