# 0010. Key positions by vehicle and process them in one loop

- **Status:** Accepted
- **Date:** 2026-11-05

## Context

The Processor judges each position against the vehicle's previous ones, so one vehicle's positions must arrive in order.
Kafka keeps order only within a partition. Vehicle ids are unique only within a feed. Later tasks need more than one
vehicle at a time: bunching (task 2.9) compares all the vehicles of a line, and a line's vehicles are spread over
partitions. The target is at least 12 000 positions/s in `positions.raw` on a replay at ten times the peak (C2).

## Decision

- **Message key `{FeedId}/{VehicleId}`**, written by the Ingestor; the Processor uses the same partitioner, so a vehicle
  keeps its partition number from topic to topic.
- **One consumer loop per process, on its own thread, for all the partitions it holds.** State is kept per partition
  (`PartitionState`), so it can leave with a partition in a rebalance, but there are no locks.
- **Scaling is by more instances in the consumer group**, with the cooperative assignor, so that a rebalance moves only the
  partitions changing owner and the others keep their state.

Measured on one hour of the HSL archive read at full speed (`docs/benchmarks.md`, "Processor throughput"): about 115 000
messages/s on one core of a development PC, about ten times the target. This has not been measured on the target server.

## Alternatives considered

- **Key by route or by feed.** Bunching would find a line's vehicles together, but a vehicle changes route between trips,
  which would break its order, and one busy route would fill one partition.
- **One thread per partition.** More throughput from one process, but bunching would need locks or messages between the
  threads. The measurement shows that the extra throughput is not needed now, and state split by partition leaves this
  open if a future measurement says otherwise.
- **Automatic offset commit.** Simpler, but it commits on a timer without knowing what was delivered, so a crash could lose
  messages. Offsets are committed by hand after a complete flush; the delivery guarantee itself is the subject of a separate ADR.

## Consequences

- Each vehicle is ordered, and no vehicle depends on a lock or on another thread.
- One slow message delays every partition of the process. The loop does no network calls per message, so this stays cheap
  until later tasks add work to it; the lag metrics show it if it stops being so.
- With several instances, the vehicles of a line can be in different processes, so bunching (2.9) will have to handle a
  line it sees only partly. That is not solved here.
- A process cannot use more than about one core for processing.
