# Live lag sampling

Local helpers for task 2.11: sample Processor `lag_time` (`dotnet-counters collect`) and Kafka consumer-group lag
(`rpk group describe`) while Aspire AppHost runs Ingestor `full` + Processor.

```powershell
# AppHost already running, Processor PID known:
dotnet-counters collect -p <pid> --counters TransitPulse.Processor --format csv -o counters --duration 00:01:05:00 --refresh-interval 5
powershell -NoProfile -File sample.ps1 -Minutes 65
```

CSV/JSONL outputs are gitignored; numbers go into `docs/benchmarks.md`.
