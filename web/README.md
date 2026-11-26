# Transit Pulse web

React + TypeScript + Vite live map (stage 3.6–3.7). MapLibre GL JS **v5**
(`react-map-gl`) shows Helsinki vehicles over SignalR viewport deltas from
`TransitPulse.Api` (`/hubs/vehicles`, ADR-0017). Marker colours encode delay; the
client drops deltas whose `generation` does not match the last `Subscribe`.

Stage 3.7 adds:

- GTFS stop markers (boarding stops) → click opens a panel of **observed** stop
  events from `GET /api/v1/feeds/{feed}/stops/{id}/events`.
- Vehicle or route-link click → **route mode**: SignalR `Subscribe` with
  `routeId`, optional shape polyline, and punctuality from
  `GET /api/v1/feeds/{feed}/routes/{id}/punctuality`.
- Loading / empty / error copy in the panels (no silent failure).

Stage 3.8: open the app with `?bench=1` to sample source→map latency
(`observedAt` → browser) on accepted SignalR updates. Method and numbers live in
`docs/benchmarks.md`.

MapLibre **v6** is not used yet: under Vite its separate ESM worker fails to fetch
vector tiles (blank map). Stay on `maplibre-gl@5` until a verified `setWorkerUrl`
setup lands.

This app is separate from `tools/map-spot-check` (offline stage-2 acceptance).

## Run with Aspire (preferred)

From the repository root, with Docker and user-secrets already set:

```powershell
dotnet run --project src/TransitPulse.AppHost
```

The AppHost starts the API and a Vite resource (`web`). Open the `web` endpoint
from the Aspire dashboard. Vite proxies `/api`, `/hubs`, `/health` and `/openapi`
to the API (including WebSockets), so the browser stays same-origin and needs no
CORS.

## Dev pair without Aspire Vite

With the API already listening (e.g. Aspire without the frontend, or a direct API
run):

```powershell
cd web
$env:VITE_API_PROXY = "http://localhost:<api-port>"
npm install
npm run dev
```

Then open the Vite URL (default `http://localhost:5173`).

## Production-shaped static build

```powershell
cd web
npm ci
npm run build
```

Output is `web/dist/`. Serve that folder with Caddy (stage 3.9) or any static host
that forwards `/api` and `/hubs` (WebSocket) to the API. Relative hub URL
`/hubs/vehicles` keeps the same origin.

`npm run preview` also proxies `/api` and `/hubs` when `VITE_API_PROXY` (or Aspire
env) is set — same as `dev`. Without a proxy target, SignalR will not reach the API.

## Attribution

Transit data © HSL (CC BY 4.0). Map tiles OpenFreeMap / OpenStreetMap contributors.
