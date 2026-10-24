# 0005. Drop positions under backpressure and rebuild them from the raw archive

- **Status:** Accepted
- **Date:** 2026-10-24

## Context

The Ingestor stores every raw message in an hourly Parquet archive (ADR-0003). In `full` mode the same records must
also be normalized into `VehiclePosition` and published to `positions.raw`. Both outputs are fed by one reader of the
pipeline's bounded channel, which applies backpressure up to the feeds when its consumer is slow.

The two outputs fail differently. The archive writes to local disk and has never been the bottleneck; the broker is
a separate process that can restart, rebalance or be down. HSL is a push feed (~620 messages/s on average,
up to ~913/s, measured 2026-10-18…21): while the pipeline waits, the MQTT reader stops reading and messages are
lost for good. Goal C4 asks that no message is lost when any service restarts.

The archive already holds every raw record unchanged, so any normalized position can be produced again from it.

## Decision

The raw archive is the source of truth; `positions.raw` is derived from it and may lag or miss records under
backpressure. `NormalizingSink` wraps the archive: each record is archived first, then offered to a separate bounded
channel (10 000 records, about 10 s of all feeds) with `TryWrite`. When that channel is full the record is not
published, the episode is logged once with its count, and the gap is filled later by replaying the archive (task 1.9).

On shutdown the archive is closed first; the position channel then drains within its own 10 s budget and the
broker producer is flushed. A failed publish or flush is logged and never fails the process.

## Alternatives considered

- **Wait on the position channel (backpressure up to the feeds).** Keeps `positions.raw` complete while the broker is
  merely slow, but a broker outage would stop archiving too and lose live HSL messages that no replay can recover.
- **Persist pending positions locally (outbox file or queue) and publish them after the broker returns.** Gives an
  automatic catch-up, but duplicates what the archive already stores, adds a second on-disk format and its own
  recovery code; the archive plus replay covers the same failure with existing parts.
- **Publish from a separate process that tails the archive files.** Isolates the broker completely, but files are
  readable only after the hourly close (the Parquet footer is written last), so positions would be up to an hour
  late — incompatible with goal C3 (source → map p95 < 2 s).

## Consequences

- A slow or failed broker never delays or stops data collection; `archive` mode on the server is unaffected
  because the decorator is not registered there.
- `positions.raw` is complete only while the broker keeps up. After an outage someone must run replay for the
  logged period; until task 1.9 exists, such gaps stay open. The drop counter becomes a metric in task 1.8.
- Replayed positions may repeat ones already published (the deduplicator's state is in memory only), so consumers
  of `positions.raw` must stay idempotent — consistent with at-least-once delivery planned for stage 2.
- C4 holds for the Ingestor only together with the archive: "no loss" means "recoverable from the archive", not
  "always present in the topic".
