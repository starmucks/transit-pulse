# 0011. Match positions to the route in a window and re-anchor only forward

- **Status:** Accepted
- **Date:** 2026-11-08

## Context

Stop events (task 2.7) and the arrival labels of the prediction model (C5) are built from how far each vehicle has got
along its trip's line. GTFS lines pass the same place more than once: terminal loops, streets driven both ways, lines
that cross themselves. A position alone cannot tell which pass the vehicle is on. A distance that goes back would make a
stop be seen twice, and with deterministic event ids the second, wrong time would overwrite the first.

With a window only, a quarter of the off-route positions of a weekday peak hour lay on the line after a wrong first
placement. Re-anchoring after any three such positions in a row then moved the distance back 2 553 times on 14 HSL
archive hours (38.9 M positions, 25 922 trips; `docs/benchmarks.md`, "Route matching"), mostly falsely: a train keeping
its journey after the run and driving back along the line was followed back 32 times in 7 minutes. Feed headings cannot settle the direction:
HSL metro always reports 0, Entur gives a bearing for about half of the vehicles.

## Decision

Search each position in a window around the vehicle's last distance, and move the vehicle to another pass only when
several positions in a row show it moving forward there.

- **Window:** from 30 m behind the last distance to as far as the vehicle could drive since its last position on the
  line (60 m/s) plus 100 m. Beyond 50 m from the line in the window and on the whole line, a position is `OffRoute`. The
  distance never decreases except on re-anchoring.
- **Re-anchoring:** a position on the line but outside the window is `OutsideWindow`. After three in a row that have
  moved forward along the line by more than 30 m from their rearmost point, the vehicle is placed on that pass
  (`Reanchored`, so stop events start over). A vehicle driving back or standing still is not followed.
- **First position of a trip:** the first pass of the line within 50 m, not the nearest one, since trips start where
  their line starts. A vehicle first seen further on is moved forward by re-anchoring.

Thresholds are first values, to be checked against stop events (2.7) and the off-route share per mode.

## Alternatives considered

- **Nearest point of the whole line for every position.** Simple, but flips between passes at every loop and street
  driven both ways, and the distance jumps back and forth.
- **Re-anchor after any N positions on the line outside the window.** Corrects wrong first placements quickly (a bus at a
  terminal in 23 s instead of when it moves off), but follows vehicles that drive back along the line: 2 553 steps back
  against 482 now.
- **Judge direction step by step, or from the feed heading.** At one position a second a train backing away moves ~9 m
  per step, less than GPS noise; feed headings are missing or constant for part of the fleet.
- **A probabilistic matcher (hidden Markov model over candidate passes).** Handles direction and ambiguity properly, but
  is much more code and tuning than the measured problems need now.

## Consequences

- Vehicles keeping an old journey, stale fixes and backing up no longer move the distance back: 81 % fewer steps back.
- A vehicle reaching its terminal along the line's loop is placed on the right pass only when it moves off: 350 of 25 922
  trips, 127 of them wait over a minute. Their first departure may be missed; task 2.7 measures this against HSL's own
  stop events before anything is added.
- Where the line drives a street both ways and the vehicle was first seen on the later pass, the nearest pass can still
  flip (20 trips, 0.08 %). Fixing it needs the direction of travel; left until stop events show it matters.
- 0.2 % of positions are `OutsideWindow`: they have no distance and produce no stop events.
