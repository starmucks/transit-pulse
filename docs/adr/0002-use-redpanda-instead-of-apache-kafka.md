# 0002. Use Redpanda instead of Apache Kafka

- **Status:** Accepted
- **Date:** 2026-10-15

## Context

The pipeline needs a durable log with partitions, per-key ordering and consumer groups: positions keyed by `vehicleId` (brief §6.3), replay of retained data, at-least-once consumers. The Kafka protocol is the de facto standard for this and has a mature .NET client (`Confluent.Kafka`).

Constraints: production is a single VPS with ~4 GB RAM shared by Ingestor, Processor, API, Worker, Postgres, Redis and the broker (brief §5.1, phase B). Development runs locally in .NET Aspire. Expected load is ~1 000 messages/s live and ≥ 7 000/s in replay ×10 (goal C2); a single broker node is expected to handle this, to be confirmed by the replay load test (stage 8).

## Decision

Use **Redpanda** (single node) as the Kafka-API broker; all application code talks to it only through the Kafka protocol via `Confluent.Kafka`.

- Started with `--smp 1 --memory 1G` both locally and in production; the container limit is 1.5 GB to leave headroom above Redpanda's own allocation.
- Measured locally (Redpanda v26.2.4, idle, no topics): 277 MiB container memory.
- Redpanda Console is a development tool only; it is not deployed to the VPS.
- `--mode dev-container` (no fsync) is local only; production uses the same `--smp`/`--memory` without it.

## Alternatives considered

- **Apache Kafka (KRaft mode).** The reference implementation, no ZooKeeper since 4.0. Rejected: a JVM process whose heap and page cache must be tuned to fit next to six other services on 4 GB, and the extra tuning buys nothing at our load.
- **Redis Streams.** Already in the stack, so zero extra containers. Rejected: storage is bounded by RAM, and 24 h of positions (~86 million per day, brief §5; ~25 GB/day raw for HSL alone, brief §5.1) cannot fit into 4 GB, consumer-group semantics differ from Kafka, and the project would not use the Kafka protocol the brief targets.
- **RabbitMQ / NATS JetStream.** Lightweight and mature. Rejected: different protocols and semantics (queues/subjects instead of a partitioned log), so per-vehicle ordering via partition keys and offset-based replay would need to be rebuilt.

## Consequences

- Lower and predictable memory use with a hard cap; one binary, no JVM tuning.
- Code stays portable: switching to Apache Kafka is a configuration and deployment change, not a code change — as long as no Redpanda-only features are used (rule: Kafka protocol only).
- Licence: Redpanda Community Edition is source-available (BSL 1.1), free for this use; it may not be offered as a hosted streaming service. Enterprise features run as a 30-day trial and switch off afterwards; we do not depend on them.
- Single node means no replication: a disk loss on the VPS loses buffered messages. Accepted — the raw archive and replay (brief §6.2) can restore data, and retention is only 24 h–7 d.
- No Aspire integration for Redpanda: it is configured as a plain container with explicit listeners (`internal` for containers, `external` for host processes).
