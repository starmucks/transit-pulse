# HSL tram 9 (route `1009`) — one archive hour

Committed slice for `TransitPulse.Replay.Tests`: the weekday morning peak hour **2026-11-04 06:00–07:00 UTC**
(08:00–09:00 Europe/Helsinki), positions whose HFP MQTT topic reports route `1009`, plus the GTFS rows that route
needs from the snapshot active that morning (`2026-11-03T1433Z`).

| Path | Content |
|---|---|
| `archive/hsl/2026-11-04/hsl-20261104T06Z-0.parquet` | Filtered raw HFP records (~151 k), same layout as the collector |
| `gtfs/*.txt` | Route `1009` and every trip, stop, shape and service it references |
| `expected-stop-events.jsonl` | Stop events the Processor must emit (one JSON object per line, sorted by `id`) |

Rebuild from the author's full archive on `D:\TransitPulse`:

```powershell
dotnet run --project tools/SliceReplayFixture -c Release
```

Then regenerate the golden file by running the replay test with `REPLAY_UPDATE_GOLDEN=1`.

Data © HSL 2026, [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)
([hsl.fi/en/opendata](https://hsl.fi/en/opendata)).
