"""Sample 20 HSL stop events on real tracks and write a MapLibre HTML for visual spot-check."""
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
# Same map stack as stage 3 (MapLibre); OpenFreeMap serves OSM-based vector tiles, no API key.
MAP_STYLE = "https://tiles.openfreemap.org/styles/liberty"
MAPLIBRE_VERSION = "4.7.1"


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
        # GeoJSON order: [lon, lat].
        shape = [[float(r["lon"]), float(r["lat"])] for r in load_csv(TRACKS / f"{name}.shape.csv")]
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
                [float(r["lon"]), float(r["lat"])]
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
    # One MapLibre map only: browsers cap concurrent WebGL contexts (~8–16), so a 20-panel grid blanks.
    payload = json.dumps(samples)
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>Stop event spot-check (20)</title>
  <link rel="stylesheet" href="https://unpkg.com/maplibre-gl@{MAPLIBRE_VERSION}/dist/maplibre-gl.css" />
  <style>
    body {{ font-family: system-ui, sans-serif; margin: 0; background: #111; color: #eee; height: 100vh; display: flex; flex-direction: column; }}
    header {{ padding: 12px 16px 8px; flex: 0 0 auto; }}
    h1 {{ font-size: 1.1rem; margin: 0 0 4px; }}
    .legend {{ margin: 0; font-size: 12px; color: #aaa; }}
    .layout {{ flex: 1; min-height: 0; display: grid; grid-template-columns: minmax(260px, 320px) 1fr; }}
    .list {{ overflow: auto; border-right: 1px solid #333; }}
    .item {{
      display: block; width: 100%; text-align: left; padding: 10px 12px; border: 0; border-bottom: 1px solid #2a2a2a;
      background: transparent; color: inherit; font: inherit; cursor: pointer;
    }}
    .item:hover {{ background: #1c1c1c; }}
    .item.active {{ background: #243044; }}
    .map-wrap {{ position: relative; min-height: 0; }}
    #map {{ position: absolute; inset: 0; }}
    .ok {{ color: #7dffa0; }}
    .warn {{ color: #ffd27d; }}
    .bad {{ color: #ff8f8f; }}
    .marker {{
      width: 14px; height: 14px; border-radius: 50%; border: 2px solid #fff;
      box-shadow: 0 0 0 1px rgba(0,0,0,.35);
    }}
    .marker.stop {{ background: #e22; }}
    .marker.vehicle {{ background: #1c7; }}
    @media (max-width: 800px) {{
      .layout {{ grid-template-columns: 1fr; grid-template-rows: 40% 1fr; }}
      .list {{ border-right: 0; border-bottom: 1px solid #333; }}
    }}
  </style>
</head>
<body>
  <header>
    <h1>Manual spot-check: 20 random HSL stop events on fixture tracks (seed {SEED}, archive hour 2026-11-04 06Z)</h1>
    <p class="legend">MapLibre + OpenFreeMap · grey = GTFS shape · blue = GPS ±45&nbsp;s · red = stop · green = vehicle at event · one map (WebGL limit)</p>
  </header>
  <div class="layout">
    <div class="list" id="list"></div>
    <div class="map-wrap"><div id="map"></div></div>
  </div>
  <script src="https://unpkg.com/maplibre-gl@{MAPLIBRE_VERSION}/dist/maplibre-gl.js"></script>
  <script>
    const samples = {payload};
    const styleUrl = {json.dumps(MAP_STYLE)};
    const list = document.getElementById('list');
    let stopMarker, vehicleMarker, active = 0;

    function distClass(m) {{ return m <= 40 ? 'ok' : m <= 80 ? 'warn' : 'bad'; }}
    function lineFeature(coords) {{
      return {{ type: 'Feature', properties: {{}}, geometry: {{ type: 'LineString', coordinates: coords }} }};
    }}
    function emptyLine() {{ return lineFeature([]); }}
    function boundsOf(s) {{
      const b = new maplibregl.LngLatBounds([s.stop.lon, s.stop.lat], [s.vehicle.lon, s.vehicle.lat]);
      for (const c of s.window) b.extend(c);
      return b;
    }}

    samples.forEach((s, i) => {{
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'item' + (i === 0 ? ' active' : '');
      btn.innerHTML = `<strong>#${{s.index}}</strong> ${{s.track}} · <code>${{s.event}}</code> @ ${{s.stop_id}}
        · <span class="${{distClass(s.distance_m)}}">${{s.distance_m}} m</span>`;
      btn.addEventListener('click', () => show(i));
      list.appendChild(btn);
    }});

    const map = new maplibregl.Map({{
      container: 'map',
      style: styleUrl,
      center: [samples[0].stop.lon, samples[0].stop.lat],
      zoom: 14,
      attributionControl: {{ compact: true }},
    }});
    map.addControl(new maplibregl.NavigationControl({{ showCompass: false }}), 'top-right');

    function ensureLayers() {{
      if (!map.getSource('shape')) {{
        map.addSource('shape', {{ type: 'geojson', data: emptyLine() }});
        map.addLayer({{
          id: 'shape', type: 'line', source: 'shape',
          paint: {{ 'line-color': '#778', 'line-width': 3, 'line-opacity': 0.75 }},
        }});
      }}
      if (!map.getSource('window')) {{
        map.addSource('window', {{ type: 'geojson', data: emptyLine() }});
        map.addLayer({{
          id: 'window', type: 'line', source: 'window',
          paint: {{ 'line-color': '#4af', 'line-width': 4 }},
        }});
      }}
      if (!stopMarker) {{
        const stopEl = Object.assign(document.createElement('div'), {{ className: 'marker stop', title: 'stop' }});
        stopMarker = new maplibregl.Marker({{ element: stopEl }});
        const vehEl = Object.assign(document.createElement('div'), {{ className: 'marker vehicle', title: 'vehicle @ event' }});
        vehicleMarker = new maplibregl.Marker({{ element: vehEl }});
      }}
    }}

    function show(i) {{
      active = i;
      const s = samples[i];
      [...list.children].forEach((el, j) => el.classList.toggle('active', j === i));
      ensureLayers();
      map.getSource('shape').setData(s.shape.length >= 2 ? lineFeature(s.shape) : emptyLine());
      map.getSource('window').setData(s.window.length >= 2 ? lineFeature(s.window) : emptyLine());
      stopMarker.setLngLat([s.stop.lon, s.stop.lat]).addTo(map);
      vehicleMarker.setLngLat([s.vehicle.lon, s.vehicle.lat]).addTo(map);
      map.fitBounds(boundsOf(s), {{ padding: 48, maxZoom: 17, duration: 500 }});
    }}

    map.on('load', () => show(0));
    addEventListener('keydown', (e) => {{
      if (e.key === 'ArrowDown' || e.key === 'j') {{ e.preventDefault(); show(Math.min(active + 1, samples.length - 1)); }}
      if (e.key === 'ArrowUp' || e.key === 'k') {{ e.preventDefault(); show(Math.max(active - 1, 0)); }}
    }});
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
