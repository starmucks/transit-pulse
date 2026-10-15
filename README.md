# Transit Pulse

Real-time public transport monitoring on open data from Helsinki (HSL), Bergen (Entur / Skyss) and Boston (MBTA).

## Status

Early development — stage 0 "Foundation". Progress is tracked in [milestones](https://github.com/starmucks/transit-pulse/milestones).

## What it will do

- A live map of every vehicle, highlighting the late ones.
- Arrival predictions from a model trained on collected trip history, compared openly against the operators' own predictions.
- Plain-language explanations of delays, built only from real data (weather, incidents, service alerts).
- Telegram notifications for the lines and stops you care about.

## Data sources

| City | Operator | Format | Delivery |
|---|---|---|---|
| Helsinki | HSL | JSON (High-Frequency Positioning) | MQTT push |
| Bergen | Entur / Skyss | SIRI-VM XML | Incremental HTTP pull |
| Boston | MBTA | GTFS-Realtime protobuf | Full-snapshot HTTP pull |

All three are normalized into one model, so a new city means a new adapter only. Licenses and attribution are being verified ([#5](https://github.com/starmucks/transit-pulse/issues/5)).

## Build and test

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```shell
dotnet build TransitPulse.slnx
dotnet test --solution TransitPulse.slnx
```

## Run locally

Requires Docker. The AppHost starts PostgreSQL (TimescaleDB + PostGIS), Redis, Redpanda and Redpanda Console; the dashboard opens at https://localhost:17100.

```shell
dotnet dev-certs https --trust
dotnet user-secrets set "Parameters:postgres-password" "<value>" --project src/TransitPulse.AppHost
dotnet user-secrets set "Parameters:redis-password" "<value>" --project src/TransitPulse.AppHost
dotnet run --project src/TransitPulse.AppHost
```

The Postgres password is written into the data volume on first start. To change it later, also remove the volume: `docker volume rm transit-pulse-postgres` (this deletes local data).

## Documentation

- [Project brief](PROJECT_BRIEF.md) (in Russian): goals, architecture, work plan.
- [Stage specifications](docs/stages/).

## License

[MIT](LICENSE)
