# 0013. Detect bunching in one process from scheduled headways

- **Status:** Accepted
- **Date:** 2026-11-19

## Context

Positions are keyed by vehicle, so the vehicles of one line are spread over Kafka partitions. Bunching instead compares
all vehicles of a line and direction. ADR-0010 deliberately gives one process a single loop over all its assigned
partitions, but leaves the several-process case unresolved: each process would see only part of a line.

The project definition orders vehicles by distance along the route and compares their actual interval with the scheduled
one. Distance and scheduled headway have different units. A full HSL weekday replay converted the spatial gap into the
scheduled travel time from the follower's position to the leader's position. Instant threshold crossings were mostly
noise: at 25% of scheduled headway, 17 586 crossings became 3 130 episodes lasting at least 30 seconds. The lasting
episodes comprised 1 712 bus, 541 tram, 492 train and 385 metro pairs (`docs/benchmarks.md`, "Bunching threshold").

## Decision

Keep one process-wide bunching detector in the Processor's existing consumer loop. Run one Processor instance while
this design is in use.

- Compare only fresh, on-route vehicles of one feed, GTFS route, direction and shared stop pattern. Stop patterns make
  their distances comparable; route variants on different geometry do not.
- Convert the actual distance gap to time by interpolating the follower's GTFS stop times. Compare it with the difference
  between the two trips' scheduled passage times at the follower's position.
- Start bunching below 25% of scheduled headway after the condition holds for 30 seconds. End the episode only after it
  has not held for 30 seconds. Publish one `Bunching` event when the entry interval completes.
- Keep vehicle state owned by its input partition. When a partition is revoked or lost, remove its vehicles from the
  process-wide comparison before another process can build its own state.
- Use no locks and make no network call per position. Before advancing the detector, restore bounded event-time order
  with per-partition watermarks and a five-second lateness allowance. A partition at its current Kafka end does not hold
  the watermark back; a source timestamp over five seconds ahead of Kafka receipt is ignored by this detector.

## Alternatives considered

- **Key input positions by route.** Puts a line together, but breaks the ordering and state of a vehicle when it changes
  route, and lets one busy route dominate a partition. Rejected in ADR-0010.
- **Repartition progress into an internal route-keyed topic and use a second consumer.** Correct with several Processor
  instances and keeps each route in one place, but adds another durable topic, consumer, delivery boundary and recovery
  state. Defer until horizontal Processor scaling is measured to be necessary.
- **Keep the ordered line state in Redis.** Lets every process see all vehicles, but adds a network read/write to the hot
  path for every position and makes one episode a distributed concurrency problem.
- **Compare raw metres with a fixed distance.** Simple, but the same distance means very different headway on a fast train
  and a slow city bus, and ignores the service frequency that defines bunching.

## Consequences

- The detector reuses the existing map-matched distance and GTFS timetable without a new topic or service.
- One loop and the event-time buffer keep cross-partition comparisons deterministic and lock-free. The buffer holds only
  positions not yet behind the slowest active partition's watermark.
- A rebalance drops comparisons involving moved partitions; a continuing episode must satisfy the 30-second entry rule
  again. Stage 2.10 will snapshot vehicle and episode state together with offsets.
- Running several Processor instances undercounts bunching between vehicles assigned to different processes. Deployment
  must remain a singleton until progress is repartitioned by route or an equivalent shared-state design supersedes this
  ADR.
