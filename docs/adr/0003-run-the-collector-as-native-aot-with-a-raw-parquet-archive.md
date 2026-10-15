# 0003. Run the collector as a Native AOT service with a raw Parquet archive

- **Status:** Accepted
- **Date:** 2026-10-15

## Context

History for the arrival model (goal C5) must accumulate 24/7 from the first days of the project, before normalization exists. During development (phase A, brief §5.1) only the collector runs on the server: 1 vCPU, 1 GB RAM, 10 GB NVMe. Everything else runs locally.

Measured in stage 1 (task 1.1): HSL delivers ~1 200 messages/s in the daytime; Parquet with zstd compresses the raw stream ~19×, about 1.2–2 GB per day for HSL alone. An idle Native AOT build of the Ingestor uses ~19 MB RSS. The linux-x64 binary is ~13 MB.

Two decisions are needed now: how the collector runs on the server, and in what form raw data is stored.

## Decision

Publish the Ingestor as a **single Native AOT binary run by systemd** (automatic restart, memory limit). Store **every message exactly as received** in **Parquet + zstd, one file per feed per UTC hour**.

- A record holds the receive time, the MQTT topic (or null for polled feeds) and the payload bytes. Normalization reads this archive later and can be fixed and replayed (task 1.9).
- A row group is written every 16 MiB of payload, which bounds memory per open file. A file is written as `.tmp` and renamed only after its footer is written, so sync scripts never pick up an incomplete file.
- AOT publish fails on any trim or AOT warning. The only exception is Parquet.Net's schema code (IL2070, IL3050). CI publishes for linux-x64 and runs `--self-test`, which writes and reads back an archive file inside the native binary.

## Alternatives considered

- **Docker on the server.** This matches phase B (docker compose). Rejected for phase A: the Docker daemon, image layers and container logs compete for 1 GB RAM and 10 GB disk that the archive needs. Phase B keeps Docker.
- **Self-contained or framework-dependent build without AOT.** Every library works without trimming. Rejected: a larger deployment, JIT start-up and a higher memory footprint on a 1 GB machine. Kept as the fallback if a required library stops working under AOT.
- **Hourly gzip JSON-lines files.** Simpler to write and read. Rejected: gzip measured ~3.3 GB/day for HSL against ~1.4 GB/day for Parquet + zstd (brief §5.1), which does not fit a 10 GB disk alongside the other feeds.
- **Normalized positions only.** Smaller and directly queryable. Rejected: a normalization bug would lose data permanently, and the auxiliary feeds (forecasts, alerts, weather) have no normalized model yet.

## Consequences

- A tiny, fast-starting service with no runtime to install. Publishing requires a Linux build (Native AOT cannot cross-compile between operating systems): CI or a `dotnet/sdk:*-aot` container.
- A graceful stop loses nothing. A crash or power loss loses the open hour of each feed, because a Parquet file without a footer is unreadable. We accept losing up to one hour per crash; leftover `.tmp` files are handled in task 1.3.
- A new trim problem inside Parquet.Net would be hidden by the suppressed summary warning. Only the native self-test in CI guards against this.
- Raw payloads are opaque in the archive: any analysis has to go through the adapters, which is acceptable because replay goes through them anyway.
- Phase A and phase B deploy differently (systemd and compose). This is accepted until stage 8.
