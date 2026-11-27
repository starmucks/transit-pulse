# 0015. Store thinned positions in Redis GEO and Timescale

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

The live map needs the current vehicle set with low latency; history for punctuality and demos needs days of points
without keeping the full ~86M positions/day. The cold Parquet archive already holds the full stream (ADR-0003). Kafka
is only a hot buffer. Stage 3.1 created the `vehicle_positions` hypertable; the Worker must fill it and a hot layer for
SignalR viewport queries (stage 3.5). Measured HSL volume: about 1.4 GB/day Parquet; about 0.14 GB/day when thinned to
one point per 10 s per vehicle (`PROJECT_BRIEF` §5.1).

## Decision

Keep three tiers for vehicle positions:

1. **Hot — Redis:** one GEO set per feed (`transitpulse:geo:{feedId}`, member = `vehicleId`) plus a hash of map
   attributes (`transitpulse:vehicle:{feedId}:{vehicleId}`) with a 120 s TTL refreshed on each write. SignalR treats a
   missing hash as a departed vehicle.
2. **Warm — Timescale:** the Worker thins `positions.enriched` to at most one accepted point per 10 s of **event time**
   per `(feed_id, vehicle_id)`, then batch-inserts into `vehicle_positions` with `ON CONFLICT DO NOTHING` on the primary
   key `(feed_id, vehicle_id, observed_at)`.
3. **Cold — Parquet:** unchanged full-resolution archive.

Thinning state lives in the Worker process. After a restart the first points may be denser until the in-memory map
refills; that is accepted for the demo. Write order at acceptance was Postgres, then Redis, then Kafka offset commit
(at-least-once, same bar as ADR-0014). **Amended by [ADR-0018](0018-separate-hot-redis-cadence-from-warm-timescale-thinning.md)
(2026-11-27):** on each flush the map path writes **Redis (hot) first**, then Timescale (warm), then Kafka offset
commit, so a slow warm insert cannot delay the live layer. The sink stays in the Worker (ADR-0009).

## Alternatives considered

- **Postgres only (no Redis GEO):** bbox queries on a hypertable are slower and contend with warm retention; the map
  tick needs sub-second reads of the current set.
- **Store every enriched point in Timescale:** exceeds the VPS disk budget and fights compression/retention for little
  map value; the archive already has full resolution.
- **Durable thinning keys in Redis:** survives Worker restarts without densification, but adds a round-trip on the hot
  path and another failure mode; in-memory thinning is enough for stage 3.

## Consequences

- The map and API read Redis for “now”; Timescale serves recent history and aggregates.
- Redis GEO members can outlive expired hashes until SignalR (or a later cleanup) removes them; readers must check the
  hash.
- A Worker crash can briefly densify the warm series; primary-key idempotency still prevents duplicate identical points.
- Volume estimates belong in `docs/benchmarks.md` as measured or clearly labeled brief-derived figures.
