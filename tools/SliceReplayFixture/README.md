# SliceReplayFixture

Builds the committed archive + GTFS slice under `tests/TransitPulse.Replay.Tests/Fixtures/hsl-tram-9-hour`
from the author's full local archive (default `D:\TransitPulse`).

```powershell
dotnet run --project tools/SliceReplayFixture -c Release
# or: -- <archiveRoot> <gtfsZip> <outputDir> [routeId...]
```

Not part of CI; run only when refreshing the fixture.
