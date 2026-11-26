# TransitPulse.Worker development notes

- **Three jobs in one process:** GTFS import on a timer, `StopEventSink` on `events.stop`, and `PositionSink` on
  `positions.enriched`. They share Postgres; Redis is required for the position GEO layer. They do not call each other.
- **Schema first.** The importer and both sinks call `SchemaMigrator` on start; the advisory lock serialises them.
- **Upsert by event id** (`StopEventStore`). Kafka delivery is at-least-once, so a Processor restart may repeat an event;
  the id is deterministic and the second write must not create a second row (ADR-0014).
- **Thinned positions** (`PositionSink`, ADR-0015 / ADR-0018): hot Redis thinner (~250 ms event time) and warm Timescale
  thinner (10 s) share one consume loop; buffers flush about every 250 ms (Postgres then Redis); Kafka offsets still
  commit on `Kafka:CommitInterval`. `ON CONFLICT DO NOTHING` on warm PK; GEO member overwrite on Redis.
- **Commit offsets only after a successful write.** An automatic commit would lose an event that never reached storage.
- **Kafka and Redis are required.** The AppHost passes `Kafka:BootstrapServers` and the `redis` connection; without them
  the host fails validation on start.
