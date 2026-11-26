# 0017. Stream map vehicles as viewport deltas over SignalR

- **Status:** Accepted
- **Date:** 2026-11-26

## Context

The live map (stage 3) must show hundreds of HSL vehicles without flooding the browser. Brief ADR 7 and
`PROJECT_BRIEF` §6.6 require SignalR with a ~1 Hz update, Redis as the hot position source (ADR-0015), and a Redis
backplane when more than one API process runs. City-wide broadcast of every position each second would overwhelm both
clients and the demo host (~4 GB RAM, phase B).

## Decision

Each SignalR connection holds **one** subscription: a feed id plus a geographic bbox and/or a `routeId`. A single
hosted ticker (~1 s, `TimeProvider`) reads Redis GEO for that viewport, joins attribute hashes, drops members whose
hash has expired, optionally filters by route, then sends only the **delta** (`added` / `updated` / `removed`) to that
connection — never `Clients.All` for positions. Redis SignalR backplane is registered even for a one-process demo.
Guards: max bbox span 0.5° in latitude and longitude; max 500 vehicles per frame (excess truncated); route-only
subscriptions query a fixed Helsinki covering bbox then filter `route_id`.

## Alternatives considered

- **Broadcast every vehicle to every client:** simplest hub, but the browser and payload size fail under peak HSL load;
  contradicts the brief.
- **Client polls REST for the full set:** workable for a handful of vehicles, but repeats full snapshots, wastes
  bandwidth, and still needs server-side bbox filtering; SignalR deltas keep the wire small and push latency low for C3.
- **One SignalR group per map tile:** scales fan-out, but adds tile math and group churn for little gain on a single-city
  demo; per-connection subscriptions are enough for stage 3.

## Consequences

- The API depends on Redis for both HybridCache L2 and the map tick; a Redis outage freezes the map (fail closed for
  live markers).
- GEO members can outlive hashes (ADR-0015); the ticker treats a missing hash as removal.
- Front-end (stage 3.6) must call `Subscribe` on pan/zoom and handle reconnect by re-subscribing.
- Metrics for subscriber count, delta size, and truncations are required so a too-wide viewport is visible in ops.
