# Real vehicle tracks on their trips' lines

One trip of one HSL vehicle each, from the raw archive hour 2026-11-04 06:00 UTC (a weekday morning peak), with the
GTFS shape of that trip from the HSL snapshot loaded that day. Used by `RouteFixtureTests` to check route matching
(task 2.6) on what synthetic lines lack: GPS noise at stations, lines that cross themselves, terminals, detours.

- `<name>.track.csv` — `seconds,lat,lon`: every `VP` position of the vehicle on that trip, seconds from its first one
  (HFP `tst`), in time order. Positions with `loc` `ODO` are left out, as the Ingestor drops them. Repeats and
  sub-second steps are kept: the test passes every row through `VehicleTrack` first, as the Processor does.
- `<name>.shape.csv` — `lat,lon`: the trip's shape points in `shape_pt_sequence` order.

| Name | Vehicle | HFP journey (route/direction/date/start) | GTFS `trip_id` | Shape length | Why it is here |
|---|---|---|---|---|---|
| `bus` | 18/165 | `9841/0/20261104/08:00:00` | `9841_20261030_Ke_1_0800` | 30 351 m | A long bus trip, end to end |
| `tram` | 40/415 | `1001/0/20261104/08:01:00` | `1001_20261102_Ke_1_0801` | 9 901 m | A tram on city streets |
| `metro` | 50/141 | `31M2/1/20261104/08:12:00` | `31M2_20261030_Ke_2_0812` | 25 784 m | Metro: whole-second fixes, one fix at a station outside the window |
| `loop` | 54/10 | `1817/0/20261104/08:00:00` | `1817_20261030_Ke_1_0800` | 3 918 m | The line crosses itself between ~560 and ~1 680 m |
| `detour` | 18/479 | `2214/1/20261104/08:09:00` | `2214_20261030_Ke_2_0809` | 8 831 m | About 4 min off the line near its end, then back on it |
| `terminal` | 12/2626 | `2159/0/20261104/08:23:00` | `2159_20261030_Ke_1_0823` | 8 569 m | First fix lands ~230 m along the line; without re-anchoring the next 192 s were off route |

Data © HSL 2026, [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) ([hsl.fi/en/opendata](https://hsl.fi/en/opendata)).
