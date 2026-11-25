# TransitPulse.Api development notes

- **Fourth deployable unit** (ADR-0009): REST catalogue for the live map. No SignalR, arrivals or AI here — those are
  later stage-3 tasks.
- **Read-only GTFS.** `GtfsCatalogueReader` locks `gtfs.routes` / `stops` / `trips` / `shapes` in ACCESS SHARE, then
  chooses the active snapshot, then reads (same rule as `TripScheduleReader`, ADR-0008). Never cache a `snapshot_id`
  across requests without putting it in the cache key.
- **HybridCache.** L1 in-process + L2 Redis (`AddRedisDistributedCache`). Keys are `gtfs:{feedId}:{snapshotId}:…`.
  The factory reads that fixed snapshot id (not “active” again). Missing active snapshot is **not** looked up in the
  cache. If the pinned snapshot is dropped mid-read, the factory fails (not stored) and the API returns 404 — never an
  empty catalogue under that key.
- **Paths.** Versioned under `/api/v1/feeds/{feedId}/…`. OpenAPI document at `/openapi/v1.json`. Health at `/health`.
