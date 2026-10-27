# 0006. Normalize every feed into one VehiclePosition model

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Stage 1 collects positions from three cities that share neither format nor protocol: HSL pushes JSON over MQTT,
Entur answers incremental SIRI-VM XML polls, MBTA serves full GTFS-RT protobuf snapshots. Goal C2a asks that a new
source is a new adapter, with no change to Processor or API.

The feeds differ in more than syntax. Vehicle ids are `oper`/`veh`, `VehicleRef` and `vehicle.id`. HSL's `dl` is
positive when ahead, SIRI `Delay` is an `xs:duration` positive when late, and MBTA sends no delay at all (none in
1.83 M trip updates of 2026-10-26). HSL reports occupancy 0 for every vehicle by default, and unset GTFS-RT fields
read as 0. A new SIRI subscription replays hours-old records; a GTFS-RT snapshot keeps vehicles that stopped
reporting (1.7% of MBTA records are older than 5 min) and repeats unchanged ones (23%). The raw archive stores every
message as received (ADR-0003), so normalization can be run again after a fix.

## Decision

Delivery and format are separate: an `IFeedSource` (MQTT or HTTP polling) only produces raw records, and one
`IPositionNormalizer` per format turns them into the shared `VehiclePosition`; rules common to all feeds live in
`PositionRouter`. A GTFS-RT city is one more normalizer instance; any other format is one more normalizer. Every
adapter follows these conventions:

- **Unknown is `null`.** A source's default or placeholder (HSL occupancy 0, unset GTFS-RT speed or bearing,
  `NO_DATA_AVAILABLE`) and an implausible value (delay beyond ±2 h) become `null`; the position itself is kept.
- **One meaning per field.** Delay is in seconds, positive when late; time is UTC; speed is m/s; occupancy is the
  GTFS-RT scale. A delay the feed does not send is not derived in the Ingestor: MBTA's `DelaySec` stays `null` and
  Processor computes it against the schedule.
- **Source ids as sent.** `VehicleId` is unique within its `FeedId`. `TripId` is the id the city's schedule uses
  where the feed has one (Entur `SKY:ServiceJourney:…`, MBTA `trip_id` including `ADDED-…` and `…-OL1`), and the
  GTFS-RT trip descriptor `{route}/{direction}/{date}/{start}` for HSL, which sends no `trip_id`.
- **Freshness and repeats are shared rules.** Records measured over 5 min before receipt are dropped by the
  polling adapters, over 5 min after receipt by the router; a per-vehicle deduplicator passes only strictly newer
  `ObservedAt`. Snapshot deltas and SIRI's full replay need no adapter code.

## Alternatives considered

- **One class per source doing both transport and parsing.** Simpler to start, but every pull feed would repeat
  polling, ETag and retry code, and replay (task 1.9) needs parsing without a transport.
- **Carry source-specific fields in the model** (per-city subtypes or a key-value bag for `InCongestion`, doors,
  carriage load). Processor and API would branch per city, which C2a rules out; such fields stay in the raw archive
  and can become a typed nullable field once a consumer needs one.
- **Resolve every `TripId` to a schedule `trip_id` in the Ingestor.** Gives comparable ids at once, but needs the
  static schedules (HSL `stop_times.txt` ≈ 940 MB) on the 2 GB collector; Processor loads them anyway (stage 2).
- **Keep source defaults as values** (0 speed, empty vehicle). Fewer nulls, but a parked bus and an unset field
  become the same number and would distort the arrival model's features (C5).

## Consequences

- A new city touches only its normalizer, its contract tests and one registration; MBTA was connected this way.
- Normalizers are pure functions of a raw record, so the archive can be replayed through the same code.
- `TripId` means different things per feed: Processor must resolve it per `FeedId`, and added MBTA trips have no
  schedule, so no delay can be computed for them.
- Consumers must handle many `null`s (MBTA delay always, SIRI speed always); fields outside the model reach no one
  until it is extended.
- The 5 min and 2 h limits are measured on October 2026 data; per-reason drop counters (task 1.8) show when they
  need revisiting.
