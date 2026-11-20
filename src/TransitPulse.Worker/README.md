# TransitPulse.Worker development notes

- **Two jobs in one process:** GTFS import on a timer and `StopEventSink` on `events.stop`. They share Postgres; they do
  not call each other.
- **Schema first.** Both the importer and the sink call `SchemaMigrator` on start; the advisory lock serialises them.
- **Upsert by event id** (`StopEventStore`). Kafka delivery is at-least-once, so a Processor restart may repeat an event;
  the id is deterministic and the second write must not create a second row (ADR-0014).
- **Commit offsets only after a successful upsert.** An automatic commit would lose an event that never reached Postgres.
- **Kafka is required.** The AppHost passes `Kafka:BootstrapServers`; without it the host fails validation on start.
