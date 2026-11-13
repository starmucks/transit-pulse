# 0012. Split the HSL position feed over several MQTT connections

- **Status:** Accepted
- **Date:** 2026-11-13

## Context

The collector receives HSL positions over one MQTT connection: ~1 420 messages/s, about 600 kB/s at the
weekday peak. Every lost second of the stream is a lost second of training history (goal C5). From 2026-11-13
11:40 UTC the broker closed this connection every 40–180 s. The archive kept 60–77 % of the daytime stream, and the
share fell as the stream grew.

Measured on the server (read-only):

- **The application keeps up:** the socket's receive queue is ~0, and CPU is 97 % idle.
- **Packets are lost before the VM:** the vNIC drops nothing, but 3.4 % of TCP segments arrive out of order.
  Ping from the server loses 3–14 % to the HSL broker, `1.1.1.1` and `8.8.8.8` alike.
- **Loss slows a single TCP flow:** the flow runs at 280–650 kB/s, below the peak stream. The broker's queue for
  us overflows, and it drops us as a slow consumer.

A PC subscriber on a different path received the full stream in the same minutes.

Test, 15:48–15:53 UTC: the same stream over four connections (bus direction 1, bus direction 2, tram, other modes).

| | Messages | Drops |
|---|---|---|
| Server, 4 connections | 425 792 | 0 |
| PC, same minutes | 424 699 | — |

Each flow ran at 60–190 kB/s. In those same minutes the collector's single connection still dropped, and ping loss
was 10–14 %.

## Decision

An MQTT feed may list several connections, each with its own disjoint topic filters. All of them feed one feed id,
so the archive, its files, normalization and keys do not change.

`hsl` uses four connections:

| Connection | Peak load |
|---|---|
| Bus, direction 1 | ~420 msg/s |
| Bus, direction 2 | ~390 msg/s |
| Other modes, direction 1 | ~300 msg/s |
| Other modes, direction 2 | ~300 msg/s |

Every connection needs traffic all night: an idle connection is taken for a lost subscription after
`SilenceTimeout` (60 s) and reconnected. On four archived nights (weekday, Saturday, two Sundays), none of the four
groups was silent for more than 30 s, and those 30 s were our own reconnects. A vehicle keeps its mode, and changes
direction only between trips, so its messages stay in order.

How failures are handled:

- A connection that drops after a healthy run reconnects at once, alone.
- A connection that fails to set up stops the others. The pipeline then restarts the feed as a whole after its
  backoff (ADR-0007).

## Alternatives considered

- **Move to another server or hoster.** It removes the cause rather than working around it, but it costs money and
  a reinstall. The hoster has not answered yet. This stays open for stage 3, when the live map needs the same
  network.
- **Split by mode (bus ×2, tram, rest).** This is what was tested. Rejected for production because trams stop at
  night: the tram connection was silent for up to 61 min (Sunday night). It would reconnect every minute and flood
  the drop alarm.
- **Split by direction only (two connections).** The filters cover every mode by construction. Rejected because
  each flow would need ~290 kB/s, which is at the observed worst single-flow rate.
- **Retry each connection on its own backoff.** This would keep the other connections alive while one fails to set
  up. But it duplicates the pipeline's backoff, and a failed setup usually hits all connections at once, since they
  share the broker and the path.

## Consequences

- The daytime stream fits through the lossy path with headroom: the busiest flow needs about a third of what one
  flow sustained at its worst.
- MQTT has no "all modes except bus" filter, so the other modes are listed by name. The archive shows bus, tram,
  train, metro and ferry with directions 1 and 2; `ubus` and `robot` come from the HFP documentation. A mode HSL adds
  later is not received until it is listed. A test checks that the shipped filters cover every listed mode in both
  directions.
- There are five broker connections instead of two. `health.sh` counts the expected ones from the configuration,
  and logs name the connection ("connection 2 of 4").
- A failed setup of one connection costs the others a restart delay (5 s at first). This is rare next to ordinary
  drops, which stay per connection.
