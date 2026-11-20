# TransitPulse.Processor development notes

- **Commit only after a complete flush and a Redis checkpoint** (ADR-0014). `CommitProcessed` flushes every output
  (enriched positions, stop events, anomalies and DLQ), saves the process checkpoint, then commits offsets. Never enable
  the automatic commit.
- **Missing checkpoint after committed offsets fails closed.** A cold assign with Kafka commits but no Redis checkpoint
  (or no state for a returning partition) stops the process; it must not resume with empty in-memory state.
- **A commit stores the next offset to read:** the last processed offset + 1 (`PartitionState.NextOffset`).
- **A message the broker never accepted stops the process** (`ThrowIfFailed`). Skipping it and committing further would
  lose it; a restart repeats everything since the last commit. An unreadable message is the opposite: after
  `Kafka:ProcessingAttempts` decode failures it is published to `positions.raw.dlq` with the reason, the offset moves and
  the stream continues.
- **One loop, no locks** (ADR-0010). `PartitionState` and `_partitions` are touched only by the loop thread. Rebalance
  handlers run on that thread too, inside `Consume` or `Close`. Delivery reports and client errors arrive on the client's
  thread and use `Interlocked`/`Volatile`.
- **No network call per message** in the loop. `GetWatermarkOffsets` reads the client's cache; `QueryWatermarkOffsets` would
  wait for the broker.
- **The stream never waits for the schedule.** `TripScheduleRefresher` builds a new `TripSchedule` off the loop and swaps it in
  whole; a failed read leaves the old one, or `no_schedule`. Never query Postgres from the loop, and never fail the loop over
  a schedule. The one wait is at start, bounded by `Schedule:StartupWait`; a longer first read finishes in the background.
- **Group by line through the found trip,** not by `VehiclePosition.RouteId`: HFP sends route variants and codes of trams not
  in service there (`docs/benchmarks.md`, "Trip lookup").
- **`positions.enriched` keeps the normalized source position intact.** Its top-level delay prefers the source value and
  otherwise carries the last delay observed from an actual arrival. This preserves HSL `dl` for comparison and gives MBTA
  a delay after its first observed arrival without changing the common `positions.raw` contract.
- **GTFS time belongs to a service day, not a calendar timestamp.** Convert it from local noon minus twelve hours, as the
  GTFS Time type specifies; hours may exceed 24 and daylight-saving transition days do not start at local midnight.
- **Stop geometry and stop times have different sharing.** Trips through the same stops on the same shape share one
  `StopPattern`, but each GTFS `trip_id` keeps its own `StopTimetable`; calendar instances of that trip share the timetable.
- **Bunching is process-wide and the Processor is a singleton for now** (ADR-0013). Comparable vehicles share a feed,
  route, direction and `StopPattern`. Their spatial gap is converted to scheduled travel time; below 25 % of planned
  headway for 30 s opens one episode, and 30 s of recovery closes it. A per-partition watermark restores event-time order
  before comparison; Kafka partition EOF releases idle partitions. Revoke/lost removes that partition's queued and active
  vehicles.
- **State belongs to a partition:** it is dropped when the partition is revoked or lost. A lost partition is never committed:
  it already belongs to another consumer.
- **Same partitioner as the Ingestor** (`Murmur2Random`): a vehicle keeps its partition number from topic to topic.
- **Log arguments must be cheap** (CA1873): compute them into locals, or pass the collection itself.
- **Measure with a Release build**, and on separate topics and a new group, so that the main topics stay as they are.
- **Tests:** logic in `tests/TransitPulse.Processor.Tests` (no Docker); broker behaviour in
  `tests/TransitPulse.Integration.Tests` (Testcontainers).
