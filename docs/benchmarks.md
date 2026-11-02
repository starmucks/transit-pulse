# Benchmarks

Measured numbers only. Each section says what was measured, on which data or machine, how, and when.

## Daily flow profile

What the collector receives over a day, from the raw archive the collector server wrote (stage 1).

**Data:** 2026-10-22 00:00 UTC to 2026-10-29 00:00 UTC: five weekdays (Thu 22, Fri 23, Mon 26, Tue 27, Wed 28) and
one weekend (Sat 24, Sun 25). The first full week after the collector moved to its current server.

**How:**

```
TransitPulse.Ingestor --profile <archive> 2026-10-22 2026-10-29
```

Records per archive hour come from the Parquet row group metadata, bytes from the file sizes; no payload is read.
Hours are shown in each city's local time (daylight saving time ended in Europe on 25.10, and the repeated local hour
counts as one more sample). Every hour of the range counts, and an hour without data counts as zero. The range has
three short collector outages: about 3.5 min around 22.10 07:00 UTC, about 10 s on 23.10 00:04 UTC and 49 s on
24.10 20:49 UTC (a deploy). Each lowers its hour by less than 6 %, and the daily figures by less than 0.3 %.

### Helsinki (HSL): MQTT messages per second

All messages received from `/hfp/v2/journey/ongoing/vp/#`, duplicates included. Average over the days of each type,
and the highest single day for that hour. Local time is Europe/Helsinki.

| Hour | Weekday avg | Weekday max | Weekend avg | Weekend max |
|---|---:|---:|---:|---:|
| 00:00 | 304 | 309 | 330 | 332 |
| 01:00 | 194 | 201 | 253 | 255 |
| 02:00 | 63 | 65 | 169 | 171 |
| 03:00 | 30 | 31 | 121 | 141 |
| 04:00 | 109 | 114 | 78 | 118 |
| 05:00 | 607 | 613 | 200 | 303 |
| 06:00 | 1080 | 1088 | 440 | 521 |
| 07:00 | 1465 | 1474 | 546 | 591 |
| 08:00 | 1531 | 1541 | 615 | 667 |
| 09:00 | 1233 | 1358 | 731 | 812 |
| 10:00 | 1141 | 1208 | 849 | 908 |
| 11:00 | 1190 | 1202 | 947 | 1037 |
| 12:00 | 1228 | 1239 | 981 | 1074 |
| 13:00 | 1283 | 1288 | 991 | 1082 |
| 14:00 | 1417 | 1423 | 999 | 1085 |
| 15:00 | 1523 | 1536 | 981 | 1067 |
| 16:00 | 1536 | 1546 | 979 | 1065 |
| 17:00 | 1430 | 1441 | 925 | 972 |
| 18:00 | 1239 | 1249 | 925 | 957 |
| 19:00 | 1052 | 1062 | 893 | 932 |
| 20:00 | 939 | 949 | 843 | 858 |
| 21:00 | 834 | 842 | 765 | 776 |
| 22:00 | 699 | 708 | 682 | 722 |
| 23:00 | 500 | 515 | 491 | 513 |

- **Peak:** about 1 540 messages/s on weekdays at 08:00 and 16:00; about 1 000/s on weekends, from 11:00 to 16:00
  without a morning peak.
- **Low:** 30/s on weekday nights at 03:00.
- **Per day:** about 81 million messages on a weekday (943/s on average), 57 million on a weekend day.
- Weekdays repeat closely: from 06:00 to 21:00 the highest day is within about 1 % of the average, except 09:00 and
  10:00 (up to 10 %).

Bergen (Entur) and Boston (MBTA) are polled, so their record counts are poll rates (one position response every
15–30 s), not traffic. Their flow is the number of vehicle positions per response; see
[Published positions per city](#published-positions-per-city).

### Published positions per city

What the Ingestor would publish to `positions.raw`: positions per second after normalization and deduplication, the
load the Processor will see. Same data and the same local-time rules as above.

**How:**

```
TransitPulse.Ingestor --profile-positions <archive> 2026-10-22 2026-10-29
```

Every raw position record is decompressed and passed through the same normalizers and `PositionRouter` as the live
pipeline, without Kafka (66 min for the week on the development PC, Release build). Average over the days of each
type.

| Hour | Helsinki weekday | Helsinki weekend | Bergen weekday | Bergen weekend | Boston weekday | Boston weekend |
|---|---:|---:|---:|---:|---:|---:|
| 00:00 | 215 | 235 | 2.1 | 3.7 | 12.6 | 13.1 |
| 01:00 | 142 | 191 | 1.2 | 2.7 | 4.6 | 5.9 |
| 02:00 | 55 | 151 | 3.0 | 2.0 | 0.5 | 1.0 |
| 03:00 | 28 | 109 | 3.0 | 1.3 | 0.3 | 0.2 |
| 04:00 | 81 | 64 | 3.2 | 0.8 | 4.3 | 1.6 |
| 05:00 | 420 | 131 | 8.8 | 1.8 | 21.5 | 8.5 |
| 06:00 | 800 | 296 | 21.2 | 3.9 | 37.7 | 14.1 |
| 07:00 | 1126 | 365 | 32.2 | 5.3 | 43.3 | 16.5 |
| 08:00 | 1178 | 416 | 29.1 | 6.5 | 41.4 | 18.3 |
| 09:00 | 923 | 506 | 19.3 | 7.5 | 34.2 | 19.4 |
| 10:00 | 822 | 601 | 16.3 | 8.9 | 29.2 | 19.9 |
| 11:00 | 862 | 667 | 16.3 | 9.1 | 28.1 | 20.3 |
| 12:00 | 899 | 695 | 17.2 | 9.6 | 29.1 | 21.4 |
| 13:00 | 951 | 699 | 21.6 | 10.1 | 32.2 | 22.5 |
| 14:00 | 1074 | 710 | 29.9 | 9.9 | 38.5 | 22.2 |
| 15:00 | 1178 | 693 | 28.8 | 10.2 | 41.4 | 21.9 |
| 16:00 | 1181 | 694 | 24.5 | 9.7 | 42.9 | 22.4 |
| 17:00 | 1083 | 656 | 17.5 | 9.3 | 43.5 | 22.4 |
| 18:00 | 906 | 651 | 12.8 | 9.0 | 41.0 | 21.6 |
| 19:00 | 764 | 626 | 10.8 | 7.8 | 31.9 | 20.1 |
| 20:00 | 672 | 590 | 9.6 | 6.7 | 24.2 | 18.3 |
| 21:00 | 579 | 520 | 7.9 | 5.9 | 20.9 | 17.1 |
| 22:00 | 474 | 462 | 6.1 | 4.8 | 18.5 | 15.5 |
| 23:00 | 360 | 348 | 4.6 | 3.4 | 15.6 | 13.8 |

- **Helsinki peak:** about 1 180 positions/s on weekdays at 08:00 and 15:00–16:00 (highest single day 1 195/s),
  against about 1 540 messages/s: 22–32 % of HSL messages are exact repeats from the source (trams send each message
  four times) and are dropped. At 03:00 on weekdays, with no trams running, repeats fall to 3 %.
- **Bergen:** peak about 32 positions/s at 07:00 on weekdays; 1–2.5 % repeats. SIRI-VM is polled incrementally, so a
  vehicle that reports twice between two polls appears once. The poll interval changed from 15 s to 30 s on
  24.10 20:49 UTC, so the weekday figures mix two days polled every 15 s and three polled every 30 s.
- **Boston:** peak about 43 positions/s from 07:00 to 08:00 and 16:00 to 17:00 on weekdays; 18–25 % of each snapshot
  repeats a position already published (a vehicle that has not reported since the last poll), up to 60 % at night.
- **Three cities together:** about 1 240 positions/s at the weekday peak, when Helsinki 16:00, Bergen 15:00 and
  Boston 09:00–10:00 fall into the same UTC hour. Helsinki is 95 % of it.

### Archive volume per day

Parquet with zstd, as stored on the collector server. MB per day, average of the days of each type.

| Feed | Weekday | Weekend |
|---|---:|---:|
| `hsl` (positions, MQTT) | 2 762 | 1 783 |
| `entur-sky-et` (predictions, SIRI-ET) | 267 | 101 |
| `mbta-tu` (predictions, GTFS-RT) | 104 | 59 |
| `hsl-tu` (predictions, GTFS-RT) | 97 | 49 |
| `entur-sky-vm` (positions, SIRI-VM) | 78 | 29 |
| `mbta-vp` (positions, GTFS-RT) | 34 | 20 |
| `mbta-alerts` | 2.4 | 2.4 |
| `hsl-alerts`, `entur-sky-sx`, `weather` | < 0.1 | < 0.1 |
| **Total** | **3 343** | **2 043** |

About 2.9 GB a day over a week, or about 90 GB a month; HSL positions are 83 % of it. `entur-sky-vm` was polled
every 15 s until 24.10 20:49 UTC and every 30 s after, so its weekday figure mixes both rates.
