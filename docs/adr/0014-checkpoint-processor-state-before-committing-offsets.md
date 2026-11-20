# 0014. Checkpoint Processor state before committing offsets

- **Status:** Accepted
- **Date:** 2026-11-19

## Context

The Processor derives events from history kept in memory. Per-partition state contains each vehicle's plausibility track,
route progress, stop detector and observed delay. Process-wide state restores event-time order across partitions and keeps
the vehicles and confirmation timers used by bunching detection. A restart currently keeps the Kafka input position but
loses that history, so it can miss a stop or split, repeat or omit a bunching episode.

Publishing an output and committing its input offset are two separate operations. Committing first can lose an output;
publishing first can repeat it after a crash. The target is therefore at-least-once delivery with no lost events and an
idempotent receiver for the output that is persisted in Postgres. ADR-0013 keeps the Processor at one instance while its
bunching state is process-wide.

## Decision

Treat a Redis checkpoint as one versioned record containing the Processor state and the next input offset for every
partition represented by that state. At each periodic commit, graceful stop or partition revocation, the consumer loop
performs these steps in order:

1. Flush every Kafka output produced for the consumed input.
2. Atomically replace the Redis checkpoint with the corresponding state and next offsets, then wait for its local AOF
   fsync (`WAITAOF 1 0` with a bounded timeout).
3. Commit those same next offsets to the Kafka consumer group.

The checkpoint contains:

- per-partition next offsets and vehicle state: plausibility track, route progress, stop progress and delay state;
- event-time buffer ownership, watermarks, pending observations and processed-through position;
- bunching observations and entry or recovery timers, together with partition-to-vehicle ownership.

Temporary result lists, metrics and caches derived from the schedule are rebuilt rather than stored. Persisted state uses
explicit snapshot contracts with a schema version and stable schedule identifiers; it does not serialize private runtime
object graphs. Restore rebinds those identifiers to the current schedule before consumption starts.

On assignment, a compatible Redis checkpoint is authoritative because a crash may happen after step 2 but before step 3.
The consumer restores the state it owns and starts each represented partition at the checkpoint's next offset. With no
checkpoint, the first run starts with empty state at Kafka's selected offset. A missing or incompatible checkpoint after
normal operation is an explicit recovery failure, not a silent partial restore.

A checkpoint write failure prevents the Kafka commit and fails the run. A Kafka commit failure leaves a usable checkpoint;
the next assignment resumes from its offsets. Losing a partition skips both checkpointing and committing it, so its next
owner resumes from the last complete checkpoint and may repeat output. DLQ records are Kafka outputs and are covered by the
same flush. `StopEventSink` upserts deterministic event ids, making repeated stop events harmless at the Postgres boundary.

## Alternatives considered

- **Kafka transactions for consumed offsets and produced records.** They make the Kafka part atomic, but they cannot include
  the Redis state or the Postgres write. They also add a second delivery model where deterministic ids and an idempotent
  database receiver are still required.
- **Commit Kafka offsets before writing the checkpoint.** A crash between the two operations resumes after input whose
  state was never saved and can lose derived events.
- **Store state and offsets separately.** Either record can become newer than the other. There is then no unambiguous point
  from which the in-memory state and Kafka stream describe the same history.
- **Rebuild all state by replaying Kafka after every assignment.** This needs an unbounded rewind or a second offset, repeats
  an unbounded amount of output and makes recovery time depend on retained history.

## Consequences

- Every completed checkpoint describes one reproducible boundary: all earlier output is acknowledged and all later input
  can be read again. Crashes may repeat output but do not skip it.
- Redis becomes part of Processor availability. Checkpoint I/O blocks only at the existing commit boundary, never once per
  message; its size and latency must be measured under the stage replay load.
- Redis runs with AOF enabled. A successful in-memory `SET` is not enough to advance Kafka: local AOF fsync must be
  confirmed first, so a Redis or host restart cannot roll the authoritative checkpoint behind committed offsets.
- Snapshot contracts and their migration policy become maintained APIs even though they are internal to the deployment.
- The restart test must exercise crashes before and after the Redis write and compare the final `stop_events` rows with an
  uninterrupted run.
- The checkpoint layout relies on the singleton boundary in ADR-0013. Horizontal Processor scaling first requires
  route-keyed ownership or another design for the process-wide bunching state.
