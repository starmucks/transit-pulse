# 0016. Run the public demo on demand with archive replay

- **Status:** Accepted
- **Date:** 2026-11-25

## Context

Phase A kept a cheap always-on VPS only for the raw Parquet archive. That host has repeatedly lost data to
network flaps, CPU overcommit and emergency reboots; paying for another always-on collector is a poor return once the
local archive already holds enough history for a first ML train/eval split (brief §10 stage 6). The brief still needs a
**production-shaped** public demo (compose, HTTPS, ~4 GB RAM budget in §5.1 phase B), but not a machine that runs for
months. Live MQTT/HTTP on an unknown pay-per-use host can fail the same way the collector did; the archive and Ingestor
replay already exist to demonstrate without the source.

## Decision

1. **End phase A** after the current collector billing period: no replacement always-on archive VPS. The PC holds the
   full raw history; ML trains there; exclude known outage windows from training and evaluation.
2. **Public demo (phase B / task 3.9)** is an **on-demand** docker compose stack on a pay-per-use host: start before a
   demo session, tear down after. Same four units, Redpanda, Postgres/Timescale, Redis and Caddy as in the brief; no
   long-lived archive disk on that host.
3. **Default demo path is archive replay** of a small golden slice shipped with the stack (or a short-lived volume).
   Live city feeds are an optional compose profile, not the path that must work for the two-minute script.
4. Goal **C8** means a production-shaped, scripted deploy with HTTPS that can be brought up for a session — not a
   requirement to keep the stack online 24/7.

## Alternatives considered

- **Keep a cheap always-on collector and a second always-on demo VPS.** Matches the original §5.1 wording, but doubles
  cost and repeats hoster risk for little new training value once the local archive is large enough.
- **Demo only on the author’s PC (Aspire).** Fine for stage 3.1–3.8 acceptance, but does not give a public HTTPS URL
  for goal C8 / the brief’s public-link acceptance.
- **On-demand host with live feeds only.** Looks “real”, but couples the demo to the host’s uplink and to third-party
  brokers; already shown to fail for hours on the collector host.

## Consequences

- Runbooks must cover start → health → warm-up → URL and a reliable stop so billing returns to zero.
- Images and a **small** golden replay set live in a registry or release artifact; the multi-tens-of-GB PC archive never
  goes to the demo host.
- Online model refresh from fresh days stops when phase A ends; seasonal drift is accepted for the published eval table.
- Stage 3 still accepts locally first; task 3.9 implements this ADR when the local demo is stable.
- Development must not stop collection **accidentally**; ending phase A is an explicit author action under this ADR.
