# 0004. Use a minimal MQTT 3.1.1 client for the HSL feed instead of MQTTnet

- **Status:** Accepted
- **Date:** 2026-10-20

## Context

HSL publishes vehicle positions only over MQTT (`mqtt.hsl.fi:8883`, TLS, topic `/hfp/v2/journey/ongoing/vp/#`),
about 780 messages/s in the evening and up to ~913/s on a weekend day. The collector runs on a 1 vCPU, 1 GB server
next to nine polled feeds (ADR-0003), and every lost second of HFP is a lost second of training history (goal C5).

The first implementation used MQTTnet. Measured on 2026-10-17: on the server (and on a Windows PC) it received
only ~200 of the ~780 messages/s; the broker's queue for the session grew and the connection dropped about every
20 s. A standard-library Python subscriber on the same server kept up, so the limit was the client library, not
the server or the network. Idle memory of the process with MQTTnet was 64 MB.

The feed needs very little of MQTT: one subscription, QoS 0, no retained messages, no publishing, no session state.

## Decision

Implement the subset of MQTT 3.1.1 the feed needs inside the Ingestor: `MqttWire` encodes CONNECT, SUBSCRIBE and
PINGREQ and parses CONNACK, SUBACK, PUBLISH and PINGRESP; `HslMqttSource` reads 256 KB blocks from an `SslStream`
and parses every complete packet in a synchronous span loop, with a keep-alive ping at half the 60 s interval.

Measured after the change: ~705 messages/s on Windows with no disconnects, idle memory 13 MB on the server.
`MqttWire` has unit tests for every cut point of a packet split between two reads.

## Alternatives considered

- **Keep MQTTnet.** The standard choice in .NET, with full protocol support, but it lost about three of every
  four messages on the target hardware; tuning was not pursued once a plain read loop was shown to keep up.
- **Another .NET MQTT library.** Not evaluated: it carries the same risk of per-message overhead, and Native AOT
  compatibility (ADR-0003) would have to be proven again for each candidate.
- **A separate subscriber process (e.g. Python) feeding the Ingestor.** Shown to keep up, but adds a second runtime
  and an IPC hop on the 1 GB server, and splits one feed's reliability logic across two processes.

## Consequences

- The feed keeps up with the full HSL stream at a fraction of the memory, in the same AOT binary.
- Protocol robustness is now our code: reconnects, timeouts and keep-alive are not inherited from a library.
  Known gaps found in review and in production logs: a connection reset currently waits for the pipeline's 5 s
  restart delay instead of reconnecting at once (two resets on 2026-10-20), and connect/TLS handshake has no timeout.
  Both are tracked and must be fixed before this is considered done.
- Only QoS 0 is supported: messages the broker sends while we are disconnected are lost. Accepted — HFP repeats
  every vehicle's position each second, so a short gap costs seconds of data, not events.
- If HSL changes the protocol (MQTT 5 only, authentication), the client must be extended; MQTTnet would be the
  fallback to re-measure first.
