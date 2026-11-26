# TransitPulse.Infrastructure development notes

- **Reading a GTFS schedule keeps the version it chose.** In one transaction, `LOCK TABLE` the `gtfs.*` parents you read
  `IN ACCESS SHARE MODE` first, then choose the active `snapshot_id`, then read (`TripScheduleReader.ReadAsync`); or
  choose and read in one statement. Never keep a `snapshot_id` between reads. Otherwise an import drops the chosen
  version in between and the read returns nothing; REPEATABLE READ does not prevent it (ADR-0008).
- **Catalogue reads for the API** (`GtfsCatalogueReader`) use the same lock-then-active-snapshot rule on
  `routes` / `stops` / `trips` / `shapes`. HybridCache keys must include that snapshot id (see `TransitPulse.Api`).
- **The schedule read (`RunsOfDays`) changes only with a measurement on a real snapshot.** `MATERIALIZED` keeps its plan
  from flipping after `ANALYZE` (a 30 s timeout was measured); on the test slice every plan is instant, so tests cannot
  catch a slow one (`docs/benchmarks.md`, "Trip lookup").
- **`stop_events` is a Timescale hypertable** (partitioned by `at`, primary key `(id, at)`). Timescale forbids
  `UNIQUE (id)` without the time column, so `StopEventStore` takes `pg_advisory_xact_lock(hashtextextended(id))`,
  deletes any prior row for that id in a ±2 day window, then inserts. Kafka `events.stop` retention is 24 h, so
  repeats land in uncompressed chunks (compress after 7 days); do not expect delete+insert against compressed history.
  Nullable `delay_sec` feeds `punctuality_by_route_day`; the sink may leave it null until events carry delay.
  API stop-panel reads use `StopEventReader` (index `stop_events_feed_stop_at` on `(feed_id, stop_id, at DESC)`).
  Route punctuality uses `PunctualityReader` against the CAGG; missing days return zeros, not null.
- **`vehicle_positions` is the warm thinned series** (hypertable by `observed_at`, 14-day retention, compress after 2 days).
  `VehiclePositionStore` batch-inserts with `ON CONFLICT DO NOTHING` on `(feed_id, vehicle_id, observed_at)`.
  `VehicleGeoStore` keeps the hot map layer: GEO `transitpulse:geo:{feedId}` plus hash `transitpulse:vehicle:{feedId}:{vehicleId}`
  (TTL 120 s). Thinning and write order are ADR-0015. `VehicleViewportReader` GEOSEARCHes a bbox, joins hashes, and
  drops members without a live hash (SignalR map tick, ADR-0017).
