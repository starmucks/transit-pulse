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
its journey after the run and driving back along the line was followed back 32 times in 7 minutes. Feed headings cannot
settle the direction: HSL metro messages carry a heading of 0, Entur gives a bearing for about half of the vehicles.

## Decision

Search each position in a window around the vehicle's last distance, and move the vehicle to another pass only when
several positions in a row show it moving forward there.

- **Window:** from 30 m behind the last distance to as far as the vehicle could drive since its last position seen on
  the line (60 m/s) plus 100 m. The 100 m cover HSL metro, whose position stays put for seconds and then jumps ahead.
  During a detour (off the whole line) the window grows; while the vehicle is seen on the line outside the window it does
  not, so it never reaches a later pass by itself. The distance never decreases except on re-anchoring.
- **Re-anchoring:** a position on the line but outside the window is `OutsideWindow`. After three in a row that have
  moved forward along the line by more than 30 m from their rearmost point, the vehicle is placed on that pass, and
  `RouteMatch.ReanchoredByMetres` says how far, so that stop events start over. A vehicle driving back or standing still
  is not followed. The metric `transitpulse.processor.reanchors` counts them by direction.
- **First position of a trip:** the first pass of the line within 50 m, not the nearest one, since trips start where
  their line starts. A vehicle first seen further on is moved forward by re-anchoring.
- **A schedule read again** (daily, or a new snapshot) keeps each vehicle's progress on its trip.

Thresholds are first values, to be checked against stop events (2.7) and the off-route share per mode.

## Alternatives considered

- **Nearest point of the whole line for every position.** Simple, but flips between passes at every loop and street
  driven both ways, and the distance jumps back and forth.
- **Re-anchor after any N positions on the line outside the window.** Corrects wrong first placements quickly (a bus at a
  terminal in 23 s instead of when it moves off), but follows vehicles that drive back along the line: 2 553 steps back
  against 482 now.
- **Judge direction step by step, or from the feed heading.** At one position a second a train backing away moves ~9 m
  per step, less than GPS noise; feed headings are missing or constant for part of the fleet.
- **A 30 m margin ahead instead of 100 m.** Stops a single fix 100–160 m ahead from moving the vehicle (33 steps back
  after such a fix in 14 hours), but 2 % of metro positions fell outside the window and metro re-anchors rose to 4 661.
- **A probabilistic matcher (hidden Markov model over candidate passes).** Handles direction and ambiguity properly, but
  is much more code and tuning than the measured problems need now.

## Consequences

- Vehicles keeping an old journey, stale fixes and backing up no longer move the distance back: 81 % fewer steps back.
- Jumps ahead that the growing window used to take silently are now re-anchors forward (815 in 14 hours, mostly metro):
  visible, but stop events must treat a step forward as stops passed unseen, not as a new start.
- A vehicle reaching its terminal along the line's loop is placed on the right pass only when it moves off: 368 trips,
  120 of them wait over a minute. Their first departure may be missed; task 2.7 measures this against HSL's own stop
  events before anything is added.
- Where the line drives a street both ways and the vehicle was seen on the wrong pass, the nearest pass can still flip
  (39 trips, 0.15 %). Fixing it needs the direction of travel; left until stop events show it matters.
- A single fix up to 100 m past the drivable distance moves the vehicle ahead, and a step back follows (33 in 14 hours).
- 0.2 % of positions are `OutsideWindow`: they have no distance and produce no stop events. The progress is kept for one
  trip only: a vehicle whose descriptor switches back to an earlier trip starts that trip over.
