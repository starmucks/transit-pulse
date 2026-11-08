# 0008. Store GTFS snapshots as partitions, with plain SQL migrations

- **Status:** Accepted
- **Date:** 2026-11-02

## Context

The Processor (stage 2) and the API (stage 3) need each feed's schedule; stage 2 keeps it in Postgres, one version per
feed (stage-2 decision 3). The collector downloads a new zip per feed once a day. Measured on 2026-11-02: the HSL
snapshot of 2026-10-31 holds 10.67 million `stop_times` (63 MB zip, 1.5–1.8 GB in Postgres), Entur 3.80 million, MBTA
2.89 million. Exports can be broken (HSL of 2026-10-27 had empty files), so a new version must be checked before it
replaces the old one, and readers must never see half a schedule or wait for an import. The rest of the schema will
lean on Postgres-specific features too: TimescaleDB hypertables and continuous aggregates (stage 3).

## Decision

Schedules live in schema `gtfs`: every imported zip is a row of `gtfs.snapshot`, and its rows sit in its own LIST
partition (by `snapshot_id`) of each GTFS table. The schema is plain numbered SQL files applied by `SchemaMigrator`.

- **Import, one transaction:** copy each file with binary `COPY` into a plain table (`LIKE` the parent, no indexes,
  `CHECK (snapshot_id = N)`) while `GtfsSnapshotCheck` checks the rows; build the primary keys; `ATTACH PARTITION`;
  switch `is_active`. A rejected or failed import rolls back and leaves nothing. A file already active (same SHA-256)
  is skipped without reading; an old version not dropped yet becomes active again.
- **Old versions** are dropped afterwards, in their own transaction, only when `LOCK TABLE <7 parents> IN ACCESS
  EXCLUSIVE MODE NOWAIT` gets all seven at once (5 tries, 2 s apart); otherwise the next import tries again.
- **Migrations:** each script in its own transaction, recorded in `public.schema_migrations`, under a session advisory
  lock so concurrent starts apply a script once. The Worker applies them on start.

## Alternatives considered

- **EF Core migrations** (the brief's default). Partitions, `ATTACH` and hypertables would be `migrationBuilder.Sql`
  anyway, plus a DbContext, entities and generated snapshot files for tables EF never writes. The API can still map EF
  Core onto existing tables later.
- **One table per GTFS file with a `snapshot_id` column, old versions removed by `DELETE`.** Removing 10.7 million rows
  a day rewrites them into WAL, leaves dead tuples and puts VACUUM on a small VPS; dropping a partition is instant.
- **`CREATE TABLE ... PARTITION OF` before the copy.** It holds ACCESS EXCLUSIVE on the parent until commit: every
  reader of the schedule would stop for the whole import (~2 min for HSL). `ATTACH` needs only SHARE UPDATE EXCLUSIVE.
- **`DROP` under `lock_timeout`.** A `DROP` waiting for one reader queues every new reader behind it, the parents
  already dropped stay locked meanwhile (up to 6 × the timeout), and taking locks one by one can deadlock with a
  reader. `DETACH PARTITION CONCURRENTLY` avoids the queue but cannot run in a transaction and can leave a partition
  half-detached.
- **Primary key in place during the copy.** Same import time on HSL (128.7 s vs 131.3 s), but the `stop_times` index
  is 799 MB instead of 516 MB (snapshot 1806 vs 1507 MB): random inserts leave B-tree pages about two-thirds full.
- **Processor reads the zip itself.** No database step, but 10.7 million rows parsed on every start and no shared
  schedule for the API.

## Consequences

- Readers see the old or the new schedule, never a mix, if they pick the active `snapshot_id` once and filter by it
  (or read in one REPEATABLE READ transaction). An import never blocks them; dropping an old version does, for the
  milliseconds the `DROP`s take. A broken export keeps the previous schedule active.
  - *Corrected 2026-11-08 (task 2.5):* choosing the `snapshot_id` once is not enough, and REPEATABLE READ does not
    help: the import's drop locks only the partitioned parents, so it can remove the chosen version between choosing and
    reading, and the read quietly returns nothing. A reader locks the parents it reads `IN ACCESS SHARE MODE` before
    choosing (as `TripScheduleReader` does), or chooses and reads in one statement. The decision itself is unchanged.
- A fresh database whose newest export is broken has no schedule for that feed until the next export: the Worker does
  not fall back to an older file.
- Disk holds two versions of a feed while it switches (~3 GB peak for HSL today). Text keys (`trip_id`, `stop_id`)
  repeated in every `stop_times` row are most of the size; integer surrogate keys are left open until the Processor's
  queries (task 2.5) show whether the size matters.
- Primary keys are listed in the migration and again in `GtfsImporter`; a mismatch makes `ATTACH` fail ("multiple
  primary keys"), and the importer tests also count one index per partition.
- Only the newest snapshot of a feed is active; replaying an older day with its own schedule needs a later option.
- Schema changes are hand-written SQL with no model diffing.
