# Stage 3 local demo (≤ 2 minutes)

Run on the author PC with Docker and AppHost user-secrets already set.

```powershell
dotnet run --project src/TransitPulse.AppHost --launch-profile https
```

1. Open the Aspire dashboard (printed login URL), then the **`web`** endpoint.
2. **Map** — wait until the status shows vehicles in view. AppHost sets Processor `Kafka:AutoOffsetReset=Latest` for
   local demos; if GEO is stale and attrs are zero after a long pause, restart AppHost (or the Processor) so Redis
   refills.
3. **Stop** — zoom in (≥ 13), click a cream stop marker → arrivals panel (list or “No observed arrivals in the last
   hour”).
4. **Route** — click a vehicle (or a route id in the stop panel) → route shape, vehicles on that line, punctuality for
   the UTC day.
5. **Latency figure** — open the same URL with `?bench=1` and read **Source → map** p50/p95 (method and latest numbers in
   [benchmarks.md](benchmarks.md)).

Screenshot: [images/live-map-helsinki.png](images/live-map-helsinki.png).
