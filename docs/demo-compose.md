# Demo compose runbook (stage 3.9 scaffold)

Local, production-shaped stack for ADR-0016: **docker compose** with Ingestor
**replay** of the committed `hsl-tram-9-hour` golden slice, Caddy serving
`web/dist`, no VPS collector involved. Public hoster / domain / HTTPS session
URL remain a follow-up (issue #87).

## Prerequisites

- Docker with Compose v2
- .NET SDK (only if you rebuild images from a dirty tree; images build inside Docker)
- Node.js for the static web build

## Prepare once (or after fixture changes)

From the repository root:

```powershell
Copy-Item deploy/demo/.env.example deploy/demo/.env
./deploy/demo/prepare-golden.ps1

cd web
npm ci
npm run build
cd ..
Remove-Item -Recurse -Force deploy/demo/web-dist -ErrorAction SilentlyContinue
Copy-Item -Recurse web/dist deploy/demo/web-dist
```

## Start → health → warm → URL

```powershell
docker compose -f deploy/demo/docker-compose.yml --env-file deploy/demo/.env up --build -d
```

1. **Health:** `curl http://localhost:8080/health` → healthy API through Caddy.
2. **Warm-up:** wait until Worker has imported the golden GTFS zip and Ingestor
   has been replaying for ~30–60 s (`docker compose … logs -f ingestor worker`).
3. **URL:** open [http://localhost:8080](http://localhost:8080) — map over SignalR
   on the replayed tram-9 hour (`Schedule__ServiceDate=2026-11-04`).
4. Optional latency bench: [http://localhost:8080/?bench=1](http://localhost:8080/?bench=1).

## Stop (billing → 0 on a paid host)

```powershell
docker compose -f deploy/demo/docker-compose.yml --env-file deploy/demo/.env down
# Tear down volumes too when the session must leave no state:
docker compose -f deploy/demo/docker-compose.yml --env-file deploy/demo/.env down -v
```

On a pay-per-use VM, stop or delete the instance after `down -v` so charges return to zero.

## Memory budget

Hard limits in compose sum to roughly **4.7 GB** container caps (Redpanda 1.5 G +
Timescale 1 G + apps). Idle usage is lower; keep the host at **≥ 4 GB RAM** and
do not co-locate with the phase-A collector (ADR-0016).

## Not in this scaffold

- Live city feed profile (optional later)
- Real domain + Let's Encrypt (replace `:8080` in `Caddyfile` when a hoster exists)
- Image registry publish / CI deploy
- Deploy onto the archive collector VPS
