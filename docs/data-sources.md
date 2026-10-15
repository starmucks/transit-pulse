# Data sources: licences, attribution and access rules

> Checked on 2026-10-15. Terms can change without notice — re-check before each public release.

## Summary

| Source | Data we use | Licence | Key | Mandatory request rules |
|---|---|---|---|---|
| HSL (Helsinki) | HFP over MQTT, GTFS static, GTFS-RT trip updates and alerts | CC BY 4.0 | No (measured: HFP and GTFS-RT work without a key) | MQTT over TLS only (port 8883) |
| Entur (Norway, Bergen) | SIRI-VM, SIRI-ET, SIRI-SX, GTFS/NeTEx | NLOD 2.0 | No | `ET-Client-Name: <company>-<application>` header |
| MBTA (Boston) | GTFS-RT vehicle positions, trip updates, alerts; GTFS static | MassDOT Developers License Agreement | No | — |
| Digitraffic / Fintraffic (Finland) | Road weather, TMS, traffic messages | CC BY 4.0 | No | `Digitraffic-User` header; `Accept-Encoding: gzip` |
| Open-Meteo | Weather for the three cities | CC BY 4.0 | No | — |

**Not yet reviewed** (used from stage 6 as ML features): Nager.Date (public holidays), Helsinki Linked Events (city events). Check their terms before the first request.

## HSL — Helsinki Region Transport

- **Licence:** all HSL datasets and APIs are under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) (except OpenStreetMap-based Journey Planner geometry, which is ODbL; we do not use it). Source: [hsl.fi/en/opendata](https://hsl.fi/en/opendata), [Digitransit terms of use](https://digitransit.fi/en/developers/apis/7-terms-of-use/).
- **Attribution:** name HSL and the time the data was delivered, e.g. `© HSL 2026`.
- **Access:** HFP at `mqtt.hsl.fi:8883`, GTFS-RT at `realtime.hsl.fi`, GTFS static at `infopalvelut.storage.hsldev.com/gtfs/hsl.zip` — no key. Digitransit GraphQL APIs require a free subscription key — not used now.
- **Rules:** use the TLS port to protect vehicle location privacy; no published numeric rate limits, but Digitransit may rate-limit or suspend abusive clients.

## Entur — Norway (Skyss, Bergen)

- **Licence:** [Norwegian Licence for Open Government Data (NLOD) 2.0](https://data.norge.no/nlod/en/2.0). Source: [Entur developer portal](https://developer.entur.no/) (moved from developer.entur.org).
- **Attribution (NLOD default wording):** "Contains data under the Norwegian licence for Open Government data (NLOD) distributed by Entur", with links to the licence and the source where practical.
- **Mandatory header:** `ET-Client-Name: <company>-<application>` on every request; our value is `transitpulse-ingestor`. Clients without it get strict rate limiting and may be blocked. No numeric limits are published for identified clients — keep polling at 10–30 s and use incremental `requestorId` requests.

## MBTA — Boston

- **Licence:** [MassDOT Developers License Agreement](https://github.com/mbta/gtfs-documentation/blob/master/developers-license-agreement.pdf) (updated 2009-11-13), referenced from the [MBTA developer pages](https://www.mbta.com/developers): non-exclusive, limited, **revocable** right to use, reproduce and redistribute the data; combining with other data is allowed.
- **Obligations:**
  - clearly acknowledge MassDOT as the data provider;
  - do not use MassDOT/MBTA logos or trademarks with the data;
  - do not present the project as MassDOT/MBTA, their agent or partner, and give no guarantees about the data to third parties;
  - do not misrepresent the data or claim ownership of it.
- **Attribution wording (ours):** "Boston transit data provided by MassDOT/MBTA. Transit Pulse is not affiliated with or endorsed by MassDOT or the MBTA."
- **Access:** GTFS-RT files at `cdn.mbta.com/realtime/*.pb` need no key. A free key only matters for the V3 API (`api-v3.mbta.com`), which we do not use — brief §14 question 3 is closed as "not needed".

## Digitraffic — Fintraffic

- **Licence:** [CC BY 4.0](https://www.digitraffic.fi/en/terms-of-service/).
- **Attribution:** "Source: Fintraffic / digitraffic.fi, licence CC 4.0 BY", with a link to the licence and to digitraffic.fi; if the data is modified, say so.
- **Mandatory request rules** ([instructions](https://www.digitraffic.fi/en/support/instructions/)):
  - `Accept-Encoding: gzip` — required; without it the API returns `406`.
  - `Digitraffic-User: TransitPulse/<version>` — raises rate limits; no personal data in the header.
  - Limits per IP without the header: 60 requests/min for REST APIs, 5 MQTT connections/min; exceeding returns `429`.

## Open-Meteo

- **Licence:** data under [CC BY 4.0](https://open-meteo.com/en/license). Source: [open-meteo.com/en/terms](https://open-meteo.com/en/terms).
- **Free tier:** non-commercial use only (Transit Pulse is a non-commercial open-source project); limits 600 calls/min, 5 000/hour, 10 000/day, 300 000/month.
- **Attribution:** "Weather data by Open-Meteo.com" with a link.
- **Our usage:** one request per city per hour ≈ 72 calls/day — far below the limits.

## What this means for the project

1. **Attribution block** — the same text in `README.md` (section "Data sources") and in the web app footer:
   > Transit data: © HSL ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)) · contains data under the Norwegian licence for Open Government data ([NLOD](https://data.norge.no/nlod/en/2.0)) distributed by Entur · MassDOT/MBTA (not affiliated or endorsed) · Fintraffic / digitraffic.fi (CC BY 4.0) · weather data by Open-Meteo.com (CC BY 4.0). Data has been processed and normalized by Transit Pulse.
2. **Recorded responses in tests** (contract and replay fixtures) are redistribution: allowed by all licences, but each fixture folder carries a short `README.md` naming the source, licence and capture date.
3. **No logos** of MBTA/MassDOT (and, to be safe, of other operators) in the UI or README.
4. **Request identity is configuration:** `ET-Client-Name` (`transitpulse-ingestor`) and `Digitraffic-User` (`TransitPulse/<version>`) live in settings, not hard-coded; values contain no personal data.
5. **Revocable terms (MBTA)** and possible changes of any feed are covered by the raw-data archive and replay (brief §13).
