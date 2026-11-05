# 0009. Build a modular monolith with four deployable units

- **Status:** Accepted
- **Date:** 2026-11-05

## Context

The system has to run on one small server in production: about 4 GB of RAM for all services, the broker, the database and
the cache. One developer works on it. The parts differ in what they do and in how they fail: collecting from feeds must
never stop, stream processing keeps state in memory, the API answers users, and background work (imports, writing to the
database) can wait. Stage 2 is the moment this becomes real, with the Worker added in task 2.3 and the Processor in task
2.4.

## Decision

Four deployable units: **Ingestor**, **Processor**, **API**, **Worker**. Each is one process built from shared libraries
(`Domain` with no dependencies, `Infrastructure` for storage and the broker). They talk only through Kafka topics, the
database and Redis, not by calling each other, apart from the internal gRPC call of the prediction module that the brief
plans for the API. A new service needs its own ADR, showing that a module has its own reason to scale or to fail separately.

## Alternatives considered

- **One process for everything.** The least memory and the simplest deployment, but a fault in the API or a slow import
  would stop collection, and any release would interrupt a stream that cannot be recovered from the source.
- **A service per domain concept** (schedule, matching, arrivals, bunching, alerts, prediction). Each could scale alone,
  but the concepts of stream processing share one vehicle's state, so splitting them adds network calls and duplicated
  state to a path measured in milliseconds, plus memory per process that this server does not have.

## Consequences

- The Ingestor can be released and restarted without touching the rest, and it runs alone on the collector server.
- Topic formats become contracts between services; they live in `Domain` (`PositionMessage`, `Topics`).
- Some small code repeats where sharing would cost more than it saves: the Ingestor, built with Native AOT, keeps its own
  topic creation rather than depending on `Infrastructure` and its database driver.
- All four share one database. Boundaries inside it are kept by convention, not by infrastructure.
