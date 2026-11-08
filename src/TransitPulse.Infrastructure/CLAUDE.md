# Infrastructure — local rules

- **Reading a GTFS schedule keeps the version it chose.** In one transaction, `LOCK TABLE` the `gtfs.*` parents you read
  `IN ACCESS SHARE MODE` first, then choose the active `snapshot_id`, then read (`TripScheduleReader.ReadAsync`); or
  choose and read in one statement. Never keep a `snapshot_id` between reads. Otherwise an import drops the chosen
  version in between and the read returns nothing; REPEATABLE READ does not prevent it (ADR-0008).
- **The schedule read (`RunsOfDays`) changes only with a measurement on a real snapshot.** `MATERIALIZED` keeps its plan
  from flipping after `ANALYZE` (a 30 s timeout was measured); on the test slice every plan is instant, so tests cannot
  catch a slow one (`docs/benchmarks.md`, "Trip lookup").
