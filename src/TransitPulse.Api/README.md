# TransitPulse.Api development notes

- **Fourth deployable unit** (ADR-0009): REST catalogue and observed realtime reads for the live map. No SignalR or AI
  here — those are later stage-3 tasks.
- **Read-only GTFS.** `GtfsCatalogueReader` locks `gtfs.routes` / `stops` / `trips` / `shapes` in ACCESS SHARE, then
  chooses the active snapshot, then reads (same rule as `TripScheduleReader`, ADR-0008). Never cache a `snapshot_id`
  across requests without putting it in the cache key.
- **HybridCache.** L1 in-process + L2 Redis (`AddRedisDistributedCache`). Keys are `gtfs:{feedId}:{snapshotId}:…`.
  The factory reads that fixed snapshot id (not “active” again). Missing active snapshot is **not** looked up in the
  cache. If the pinned snapshot is dropped mid-read, the factory fails (not stored) and the API returns 404 — never an
  empty catalogue under that key.
- **Observed stop events (3.4), not ETAs.** `GET …/stops/{stopId}/events` returns recent rows from `stop_events`
  (default last 60 minutes via `TimeProvider`, optional `since` / `limit`). Unknown stop or missing GTFS snapshot →
  404; known stop with nothing in the window → 200 `[]`.
- **Route punctuality.** `GET …/routes/{routeId}/punctuality` reads `punctuality_by_route_day` for one UTC `day`
  (default today). Unknown route → 404; known route with no aggregate row → 200 with zero counts.
- **Paths.** Versioned under `/api/v1/feeds/{feedId}/…`. OpenAPI document at `/openapi/v1.json`. Health at `/health`.
