---
name: new-adapter
description: Checklist for connecting a new transit data feed (city or source) to the Ingestor through a feed adapter. Use when adding or changing a feed adapter (HSL MQTT, Entur SIRI, MBTA GTFS-RT or a new city).
---

# New feed adapter

> Draft: concrete paths, base classes and examples are filled in during stage 1, once the first adapter exists.

Goal: a new source changes only its adapter and configuration — never the Processor or the API.

## Checklist

1. **Research.** Format, protocol (push / incremental pull / full pull), rate limits, required headers, license and attribution. Save 2–3 real responses as test fixtures.
2. **Adapter.** An `IFeedSource` implementation in the Ingestor; streaming parsing for large payloads (e.g. `XmlReader` for SIRI, not `XDocument`).
3. **Normalization** into `VehiclePosition`:
   - timestamps converted to UTC from the source's own time zone;
   - speed in m/s, bearing in degrees, delay in seconds (ISO 8601 durations parsed);
   - occupancy mapped to `OccupancyLevel`, missing values as `null`;
   - stale records dropped by their observation time;
   - stable `VehicleId` within the feed.
4. **Contract tests** on the saved fixtures: every mapped field, missing fields, stale and malformed records.
5. **Resilience.** Reconnect / retry with backoff; metric "seconds without data" per feed; incoming messages per second.
6. **Configuration.** URL, headers (e.g. `ET-Client-Name`), polling interval — in config, no secrets in the repository.
7. **Documentation.** Attribution in `docs/data-sources.md`; an ADR if the protocol or delivery model is new for the project.
