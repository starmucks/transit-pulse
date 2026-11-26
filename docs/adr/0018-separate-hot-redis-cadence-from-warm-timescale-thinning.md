# 0018. Separate hot Redis cadence from warm Timescale thinning

- **Status:** Proposed
- **Date:** 2026-11-26

## Context

Goal C3 needs source→map p95 under 2 s. A local Aspire measurement (stage 3.8) saw p50 ~7 s / p95 ~10 s. The Worker
`PositionSink` used one 10 s event-time thinner for both Timescale and Redis and flushed only on
`Kafka:CommitInterval` (5 s), so Redis often held points several seconds old before SignalR's ~1 Hz tick. Source age
alone for HSL is already ~0.5–1.2 s (benchmarks “Source delay”), so the remaining budget for our path is under ~1 s.

## Decision

1. Keep **warm** Timescale thinning at 10 s event time (ADR-0015 volume target).
2. Add a **hot** Redis thinner (~250 ms event time) so the map layer updates more often than the warm series.
3. Flush non-empty hot/warm buffers on a short `FlushInterval` (~250 ms); commit Kafka offsets on the existing
   `CommitInterval` after successful flushes.
4. Run the SignalR map ticker at **250 ms** so the tick is not the dominant remaining delay (amends the “~1 Hz” wording
   in ADR-0017 for the live demo).

## Alternatives considered

- **Only shorten `CommitInterval` to 250 ms:** helps flush latency but Redis would still move only every 10 s of event
  time; C3 would still miss on sparse map updates.
- **Write every enriched point to Redis and Timescale:** lowest map lag, but blows the warm disk budget ADR-0015
  rejected.
- **Push SignalR from the Worker on each Redis write:** removes the poll tick, but couples Worker to API clients and
  needs a new bus; deferred.

## Consequences

- Redis write rate and SignalR frame rate rise (still viewport-scoped deltas, not city broadcast).
- Warm series density and retention math stay as in ADR-0015.
- ADR-0017’s “~1 Hz” becomes “≤ 250 ms tick” for this demo; backplane and per-connection subscription rules unchanged.
- Local Aspire sets Processor/Worker position consumers to a dedicated group with `AutoOffsetReset=Latest` so a cold
  Redpanda backlog cannot refill Redis with hours-old `observed_at` (which the bench correctly rejects as &gt; 120 s).
- Remeasured 2026-11-26 (`?bench=1`, n=2 000): p50 **3.00 s** / p95 **3.73 s** vs baseline 7.3 / 10.1 s. Segment ages
  (same window): to Redis p95 **3.41 s**, Redis→send **513 ms**, send→browser **9 ms** — remaining gap is before Redis
  (see `docs/benchmarks.md`).
