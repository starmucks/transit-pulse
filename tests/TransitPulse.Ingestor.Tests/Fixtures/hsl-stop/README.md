# HSL HFP stop event fixtures

Real stop events from the HSL High-Frequency Positioning feed (`mqtt.hsl.fi:8883`), received on 2026-11-02 around
09:03 UTC with the subscription of feed `hsl-stop` (`/hfp/v2/journey/ongoing/{arr,ars,pde,dep,pas,wait}/#`).
They are the reference arrivals that stage 2 compares its own stop detection with; the parser and its contract test
come with that detection (task 2.7).

- `stop-events.jsonl` — one message per line in the archive layout of `hsl/vp.jsonl`: `case`, `receivedAt`, MQTT
  `topic` and `payload`, the payload kept byte for byte as a string.

Each case is the whole event sequence of one vehicle at one stop, in `tst` order:

| Lines | Case | Sequence |
|---|---|---|
| 1–4 | Bus stopping | `arr` (enters the stop radius) → `ars` (stopped) → `pde` (ready to depart) → `dep` (leaves the radius) |
| 5–7 | Bus passing without stopping | `arr` → `pas` → `dep`; `pas` and `dep` within milliseconds |
| 8–11 | Tram stopping | As the bus; `pde` and `dep` 7 ms apart |
| 12–16 | Waiting at a stop | `wait` between `ars` and `pde`: held at the stop, e.g. for its timetable |
| 17–20 | Train stopping | As the bus |

Every payload carries the GTFS stop id (`stop`) and the scheduled times at that stop (`ttarr`, `ttdep`) besides the
usual position fields. Measured on 3 070 events in 90 s (34/s): 251 stop and 288 pass-through sequences; buses,
trams and trains only — metro sent no stop events.

Data © HSL 2026, [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) ([hsl.fi/en/opendata](https://hsl.fi/en/opendata)).
