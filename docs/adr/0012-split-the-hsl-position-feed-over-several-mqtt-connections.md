# 0012. Split the HSL position feed over several MQTT connections

- **Status:** Accepted
- **Date:** 2026-11-13

## Context

The collector received HSL positions over one MQTT connection: ~1 420 messages/s, about 600 kB/s at the weekday
peak. Every lost second of the stream is lost training history (goal C5). From 2026-11-13 11:40 UTC the broker
closed this connection every 40–180 s. The archive kept 60–77 % of the daytime stream, and the share fell as the
stream grew.

Measured on the server:

- **The application keeps up:** the socket's receive queue is ~0.
- **Packets are lost before the VM:** the vNIC drops nothing, but 3.4 % of TCP segments arrive out of order, and
  ping loses 3–14 % to the HSL broker, `1.1.1.1` and `8.8.8.8` alike.
- **Loss caps a single TCP flow:** the flow runs at 280–650 kB/s, below the peak stream. The broker's queue for us
  overflows, and it drops us as a slow consumer.

Test, 15:48–15:53 UTC, with ping loss at 10–14 %:

| | Messages | Drops |
|---|---|---|
| Server, 4 connections | 425 792 | 0 |
| PC on another path, same minutes | 424 699 | — |

In those same minutes the collector's single connection still dropped.

## Decision

An MQTT feed may list several connections, each with its own disjoint topic filters. All of them feed one feed id,
so the archive, normalization and keys do not change.

`hsl` uses four connections:

| Connection | Peak load |
|---|---|
| Bus, direction 1 | ~420 msg/s (~180 kB/s) |
| Bus, direction 2 | ~390 msg/s |
| Other modes, direction 1 | ~300 msg/s |
| Other modes, direction 2 | ~300 msg/s |

Every group must have traffic all night: an idle connection is taken for a lost subscription after the 60 s
`SilenceTimeout`. On the archived nights checked, no group was silent for more than 30 s, and those gaps were
reconnects that hit all groups at once. A vehicle's messages stay on one connection within a trip.

How failures are handled:

- A connection that drops after 10 s or more reconnects at once, alone.
- A connection that fails to set up, or ends within 10 s of connecting, stops the others. The pipeline restarts the
  whole feed after its backoff (ADR-0007).

## Alternatives considered

- **Move to another server.** It removes the cause instead of working around it, but it costs money and a
  reinstall. This stays open for stage 3, when the live map needs the same network.
- **Split by mode** (bus ×2, tram, rest), as in the test. Trams stop at night, and a tram-only connection was silent
  for up to 61 min. It would reconnect every minute and trip the drop alarm.
- **Split by direction only** (two connections). The filters would cover every mode by construction, but each flow
  would need ~290 kB/s: no headroom over the worst measured 280 kB/s.
- **Retry each connection on its own backoff.** This duplicates the pipeline's backoff. Connections that share a
  broker and a path are expected to fail together.

## Consequences

- The busiest flow needs ~180 kB/s, about two thirds of the worst single-flow rate measured.
- MQTT has no "all modes except bus" filter, so the other modes are listed by name. The archive shows bus, tram,
  train, metro and ferry, each with directions 1 and 2. `ubus` and `robot` are added from the HFP documentation.
  A mode HSL adds later is not received until it is listed. A test checks that the shipped filters cover every
  listed mode in both directions exactly once.
- There are five broker connections instead of two. `health.sh` counts the expected ones from the configuration,
  and logs name the connection ("connection 2 of 4").
- Short-lived connections now cost the other connections a restart delay (5 s at first). We expect this to be rare
  next to ordinary drops; `health.sh` counts both after deploy.
