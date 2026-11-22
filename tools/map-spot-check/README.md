# Stop-event map spot-check

Builds a Leaflet page of **20 random** HSL stop events (`ars` / `dep` / `pas`) from the Domain track
fixtures (`tests/TransitPulse.Domain.Tests/Fixtures/tracks`, archive hour 2026-11-04 06Z) so a reviewer can see the
GPS track, the GTFS stop and the vehicle position at the event time on OpenStreetMap.

```powershell
python tools/map-spot-check/build_spot_check.py
python -m http.server 8765 --directory tools/map-spot-check
# open http://127.0.0.1:8765/spot-check.html
```

Sampling uses a fixed seed (`20261122`) for a reproducible set. Generated `spot-check.html` is gitignored; the method
and result are in `docs/benchmarks.md` («Stop events on the map»).
