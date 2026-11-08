# 0011. Match positions to the route in a window and re-anchor only forward

- **Status:** Accepted
- **Date:** 2026-11-08

## Context

Stop events (task 2.7) and the arrival labels of the prediction model (C5) are built from how far each vehicle has got
along its trip's line. GTFS lines pass the same place more than once: terminal loops, streets driven both ways, lines
that cross themselves, so a position alone cannot tell which pass the vehicle is on. A distance that goes back would make
a stop be seen twice, and with deterministic event ids the second, wrong time would overwrite the first.

With a window only, a quarter of the off-route positions of a weekday peak hour lay on the line after a wrong first
placement. Re-anchoring after any three such positions in a row then moved the distance back 2 553 times on 14 HSL
archive hours (25 922 trips; `docs/benchmarks.md`, "Route matching"), mostly falsely: a train keeping its journey after
the run was followed back along the line 32 times in 7 minutes. Feed headings cannot settle the direction: HSL metro
messages carry a heading of 0, Entur gives a bearing for about half of the vehicles.

## Decision

Search each position in a window around the vehicle's last distance, and move the vehicle to another pass only when
several positions in a row show it moving forward there.

- **Window:** from 30 m behind the last distance to as far as the vehicle could drive since its last position seen on
  the line (60 m/s) plus 100 m, which covers HSL metro positions that stay put and then jump ahead. The window grows
  only while the vehicle is off the whole line (a detour); while it is seen on the line outside the window it does not.
  The distance never decreases except on re-anchoring.
- **Re-anchoring:** a position on the line but outside the window is `OutsideWindow`. After three in a row that have
  moved forward along the line by more than 30 m from their rearmost point, the vehicle is placed on that pass.
  `RouteMatch.ReanchoredByMetres` gives the step: back means the earlier placement was wrong and stop events start over;
  forward means the stops in between were passed unseen. The metric `transitpulse.processor.reanchors` counts both.
- **First position of a trip:** the first pass of the line within 50 m, not the nearest one, since trips start where
  their line starts. A vehicle first seen further on is moved forward by re-anchoring.
- **A schedule read again** (daily, or a new snapshot) keeps each vehicle's progress, clamped to the new shape's length.

Thresholds are first values, to be checked against stop events (2.7) and the off-route share per mode.

## Alternatives considered

- **Nearest point of the whole line for every position.** Flips between passes at every loop and street driven both
  ways; the distance jumps back and forth.
- **Re-anchor after any N positions outside the window.** Corrects a wrong first placement sooner (a bus at a terminal in
  23 s instead of when it moves off), but follows vehicles driving back: 2 553 steps back against 482.
- **Direction step by step, or from the feed heading.** At one position a second a train backing away moves ~9 m per
  step, less than GPS noise; headings are missing or constant for part of the fleet.
- **A 30 m margin ahead instead of 100 m.** Blocks single fixes 30–100 m past the drivable distance, but put 2 % of metro
  positions outside the window and raised metro re-anchors from 565 to 4 661.
- **A hidden Markov model over candidate passes.** Handles direction and ambiguity properly, at far more code and tuning
  than the measured problems need now.

## Consequences

- 81 % fewer steps back: vehicles keeping an old journey, stale fixes and backing up no longer move the distance back.
- 815 re-anchors forward in 14 hours (553 metro): jumps past the window after standing still and vehicles first seen on
  an earlier pass; part of them the growing window used to take silently.
- A vehicle reaching its terminal along the line's loop is placed right only when it moves off (368 trips, 120 waiting
  over a minute); its first departure may be missed. Task 2.7 measures this against HSL's own stop events.
- Known gaps: passes still flip on streets driven both ways (39 trips, 0.15 %); a single fix 30–100 m past the drivable
  distance is taken, then stepped back (33 times); a vehicle off the whole line in between lets the window grow, so it may
  reach a later pass; progress is kept for one trip only.
- 0.2 % of positions are `OutsideWindow`: no distance, no stop events.
