# GTFS fixtures

## `hsl-tram-9/`

A real slice of the HSL GTFS export downloaded 2026-10-31 14:33 UTC: tram route `1009` (line 9), service
`1009_20261030_20261101_Ke`, and every row its trips reference. Rows and columns are unchanged, including columns
the reader ignores and HSL's habits such as a single space for an empty `parent_station`.

| File | Rows | Content |
|---|---|---|
| `routes.txt` | 1 | Route `1009` |
| `trips.txt` | 3 | One trip per direction (14:28 and 14:52) and the last one of the day, starting `25:35:00` |
| `stop_times.txt` | 81 | All stops of the three trips |
| `stops.txt` | 54 | The stops they call at |
| `shapes.txt` | 678 | Both shapes of the trips |
| `calendar.txt` | 1 | The service |
| `calendar_dates.txt` | 0 | Header only: the service has no exceptions |

Data © HSL 2026, [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) ([hsl.fi/en/opendata](https://hsl.fi/en/opendata)).
