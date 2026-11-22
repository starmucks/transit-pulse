"""Sample 20 HSL stop events on real tracks and write a Leaflet HTML for visual spot-check."""
from __future__ import annotations

import csv
import json
import math
import random
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TRACKS = ROOT / "tests/TransitPulse.Domain.Tests/Fixtures/tracks"
OUT = Path(__file__).resolve().parent / "spot-check.html"
SEED = 20261122
N = 20
# Modes with HSL stop events in the fixtures (no metro).
NAMES = ["bus", "tram", "loop", "detour", "terminal", "train"]


def haversine_m(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    r = 6371000.0
    p1, p2 = math.radians(lat1), math.radians(lat2)
    dphi = math.radians(lat2 - lat1)
    dl = math.radians(lon2 - lon1)
    a = math.sin(dphi / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(dl / 2) ** 2
    return 2 * r * math.asin(math.sqrt(a))


def load_csv(path: Path) -> list[dict[str, str]]:
    with path.open(encoding="utf-8") as f:
        return list(csv.DictReader(f))


def nearest_track(track: list[dict[str, str]], seconds: float) -> dict[str, float]:
    best = min(track, key=lambda row: abs(float(row["seconds"]) - seconds))
    return {"seconds": float(best["seconds"]), "lat": float(best["lat"]), "lon": float(best["lon"])}


def candidates() -> list[dict]:
    rows: list[dict] = []
    for name in NAMES:
        track = load_csv(TRACKS / f"{name}.track.csv")
        stops = {r["stop_id"]: r for r in load_csv(TRACKS / f"{name}.stops.csv")}
        shape = [(float(r["lat"]), float(r["lon"])) for r in load_csv(TRACKS / f"{name}.shape.csv")]
        events = load_csv(TRACKS / f"{name}.events.csv")
        for ev in events:
            if ev["event"] not in {"ars", "dep", "pas"}:
                continue
            stop = stops.get(ev["stop_id"])
            if stop is None:
                continue
            t = float(ev["seconds"])
            pos = nearest_track(track, t)
            stop_lat, stop_lon = float(stop["lat"]), float(stop["lon"])
            dist = haversine_m(pos["lat"], pos["lon"], stop_lat, stop_lon)
            # Track window ±45 s around the event for the map polyline snippet.
            window = [
                (float(r["lat"]), float(r["lon"]))
                for r in track
                if abs(float(r["seconds"]) - t) <= 45
            ]
            rows.append(
                {
                    "track": name,
                    "event": ev["event"],
                    "stop_id": ev["stop_id"],
                    "seconds": t,
                    "vehicle": pos,
                    "stop": {"lat": stop_lat, "lon": stop_lon},
                    "distance_m": round(dist, 1),
                    "window": window,
                    "shape": shape,
                }
            )
    return rows


def sample(rows: list[dict]) -> list[dict]:
    rng = random.Random(SEED)
    # Stratify: take from each track, then fill to N.
    by_track: dict[str, list[dict]] = {n: [] for n in NAMES}
    for row in rows:
        by_track[row["track"]].append(row)
    picked: list[dict] = []
    for name in NAMES:
        pool = by_track[name]
        if not pool:
            continue
        rng.shuffle(pool)
        picked.append(pool.pop())
    remaining = [r for pool in by_track.values() for r in pool]
    rng.shuffle(remaining)
    while len(picked) < N and remaining:
        picked.append(remaining.pop())
    rng.shuffle(picked)
    for i, row in enumerate(picked[:N], start=1):
        row["index"] = i
    return picked[:N]


def render(samples: list[dict]) -> str:
    payload = json.dumps(samples)
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>Stop event spot-check (20)</title>
  <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" />
  <style>
    body {{ font-family: system-ui, sans-serif; margin: 0; background: #111; color: #eee; }}
    h1 {{ font-size: 1.1rem; padding: 12px 16px; margin: 0; }}
    .grid {{ display: grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap: 12px; padding: 12px; }}
    .card {{ background: #1b1b1b; border-radius: 8px; overflow: hidden; border: 1px solid #333; }}
    .meta {{ padding: 8px 10px; font-size: 12px; line-height: 1.4; }}
    .map {{ height: 260px; }}
    .ok {{ color: #7dffa0; }}
    .warn {{ color: #ffd27d; }}
    .bad {{ color: #ff8f8f; }}
  </style>
</head>
<body>
  <h1>Manual spot-check: 20 random HSL stop events on fixture tracks (seed {SEED}, archive hour 2026-11-04 06Z)</h1>
  <div class="grid" id="grid"></div>
  <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
  <script>
    const samples = {payload};
    const grid = document.getElementById('grid');
    for (const s of samples) {{
      const card = document.createElement('div');
      card.className = 'card';
      const cls = s.distance_m <= 40 ? 'ok' : s.distance_m <= 80 ? 'warn' : 'bad';
      card.innerHTML = `<div class="meta">
        <strong>#${{s.index}}</strong> ${{s.track}} · <code>${{s.event}}</code> @ stop ${{s.stop_id}} · t=${{s.seconds.toFixed(1)}}s
        · vehicle→stop <span class="${{cls}}">${{s.distance_m}} m</span>
      </div><div class="map" id="m${{s.index}}"></div>`;
      grid.appendChild(card);
      const map = L.map(`m${{s.index}}`, {{ zoomControl: false }});
      L.tileLayer('https://{{s}}.tile.openstreetmap.org/{{z}}/{{x}}/{{y}}.png', {{
        maxZoom: 19,
        attribution: '&copy; OpenStreetMap'
      }}).addTo(map);
      if (s.shape.length) L.polyline(s.shape, {{ color: '#668', weight: 3, opacity: 0.7 }}).addTo(map);
      if (s.window.length) L.polyline(s.window, {{ color: '#4af', weight: 4 }}).addTo(map);
      L.circleMarker([s.stop.lat, s.stop.lon], {{ radius: 7, color: '#fff', fillColor: '#e22', fillOpacity: 1 }}).addTo(map)
        .bindTooltip('stop');
      L.circleMarker([s.vehicle.lat, s.vehicle.lon], {{ radius: 6, color: '#fff', fillColor: '#1c7', fillOpacity: 1 }}).addTo(map)
        .bindTooltip('vehicle @ event');
      map.fitBounds([[s.stop.lat, s.stop.lon], [s.vehicle.lat, s.vehicle.lon], ...s.window], {{ padding: [24, 24], maxZoom: 17 }});
    }}
  </script>
</body>
</html>
"""


def main() -> None:
    samples = sample(candidates())
    OUT.write_text(render(samples), encoding="utf-8")
    summary = [
        {
            "index": s["index"],
            "track": s["track"],
            "event": s["event"],
            "stop_id": s["stop_id"],
            "distance_m": s["distance_m"],
        }
        for s in samples
    ]
    (OUT.parent / "spot-check-summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(f"Wrote {OUT} ({len(samples)} events)")
    for s in summary:
        print(f"#{s['index']:02d} {s['track']:8s} {s['event']:3s} stop={s['stop_id']} dist={s['distance_m']}m")


if __name__ == "__main__":
    main()
