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
pipeline, without Kafka (66 min for the week on a laptop with an Intel Core i7-6700HQ, 16 GB RAM, archive on a
laptop HDD; Release build). Average over the days of each
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
  against about 1 540 messages/s: the deduplicator drops 22–32 % of HSL messages. At 03:00 on weekdays, with no trams
  running, it drops 3 %. Almost all of them are byte-identical copies sent by trams (see
  [HSL repeats](#hsl-repeats)).
- **Bergen:** peak about 32 positions/s at 07:00 on weekdays; 1–2.5 % repeats. SIRI-VM is polled incrementally, so a
  vehicle that reports twice between two polls appears once. The poll interval changed from 15 s to 30 s on
  24.10 20:49 UTC, so the weekday figures mix two days polled every 15 s and three polled every 30 s.
- **Boston:** peak about 43 positions/s from 07:00 to 08:00 and 16:00 to 17:00 on weekdays; 18–25 % of each snapshot
  repeats a position already published (a vehicle that has not reported since the last poll), up to 60 % at night.
- **Three cities together:** about 1 240 positions/s at the weekday peak. This is the sum of the three weekday
  averages for the hours that fall into the same UTC hour (Helsinki 16:00, Bergen 15:00, Boston 09:00–10:00), not a
  measurement of one hour. Helsinki is 95 % of it.

### HSL repeats

Why the deduplicator drops a fifth of the HSL stream: one archive hour at the weekday peak, 2026-10-27 14:00 UTC
(16:00 in Helsinki), 5 542 643 messages. Every message whose vehicle (`oper`, `veh`) and measurement time (`tst`)
repeat an earlier message of the hour is compared with that first copy, byte for byte, payload and MQTT topic. One-off
Python script with pyarrow over the archive file.

| Mode | Messages | Repeats | Share | Byte-identical |
|---|---:|---:|---:|---:|
| tram | 1 598 473 | 1 198 858 | 75.0 % | all |
| bus | 3 461 606 | 10 | < 0.1 % | all |
| train | 330 986 | 29 | < 0.1 % | all |
| metro, ferry | 151 578 | 0 | 0 | — |
| **All** | **5 542 643** | **1 198 897** | **21.6 %** | **all** |

- Trams send each message four times (399 608 of the hour's tram measurements, a few 3 or 8 times); the copies
  arrive 11 ms after the first one at the median, 91 ms at p95. Dropping them loses no data.
- The deduplicator also drops messages older than the vehicle's last position (out of order): 0.85 % of this hour,
  13 % of metro messages. Together with the copies that is 22.5 %, the profile's weekday average for 16:00.

### Archive volume per day

Parquet with zstd, as stored on the collector server. MiB per day, average of the days of each type.

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

About 2.9 GiB (3.1 GB) a day over a week, or about 87 GiB (94 GB) a month; HSL positions are 83 % of it. `entur-sky-vm` was polled
every 15 s until 24.10 20:49 UTC and every 30 s after, so its weekday figure mixes both rates.

## GTFS snapshot check

Reading and checking whole schedule snapshots with `GtfsSnapshotCheck.Run` (stage 2, task 2.2): every file streamed
from the zip once, references checked as rows arrive.

**Data:** every snapshot the collector archived from 2026-10-21 to 2026-11-01: HSL 10, MBTA 4, Entur (Bergen) 21.
**How:** a one-off console (Release build, workstation GC) per snapshot, zip read from a local HDD on the development
PC; time is wall clock of the whole check, memory the process peak working set. Measured 2026-11-02.

| Feed | Snapshots | Valid | `stop_times.txt` rows | Other large files | Time | Peak working set |
|---|---|---|---|---|---|---|
| HSL | 10 | 9 | 10.7 M (959 MB unpacked) | 381 k trips, 468 k shape points | 8.9–27.5 s | 79–99 MiB |
| Entur (Bergen) | 21 | 21 | 3.8 M | 158 k trips, 3.8 M shape points | 5.4–42.3 s | 67–75 MiB |
| MBTA | 4 | 4 | 2.9 M | 114 k trips, 387 k shape points | 2.4–4.3 s | 53–54 MiB |

- The one rejected snapshot is HSL's export of 2026-10-27 02:30 UTC (654 kB instead of ~60 MB): rejected in 0.3 s with
  the single problem `routes.txt: the file is empty, without a header row`.
- Times vary up to eightfold for the same feed: the zip is read from a spinning disk with a cold or warm cache, so the
  range shows the disk more than the parser. Memory does not depend on file size: rows are streamed, only id sets stay.
- MBTA's 7 one-stop shuttle templates (`…-sample`, service `canonical` that runs on no day) failed the first version of
  the "at least 2 stop times" rule; the rule now applies to trips of services that run on at least one day.

## GTFS import

Importing whole snapshots into Postgres with `GtfsImporter` (stage 2, task 2.3): binary `COPY` into a new partition per
snapshot, checked in the same pass, primary keys, `ATTACH`, switch to active (ADR-0008).

**How:** the Worker (Debug build) on the development PC, one feed at a time, zip read from a local HDD; Postgres is the
AppHost's `timescale/timescaledb-ha:pg17.11-ts2.30.2` container (`--memory=1g`, Docker Desktop on WSL 2). Time is the
Worker's own measurement of `ImportAsync`; size is `pg_total_relation_size` of the snapshot's partitions. One run per
row, measured 2026-11-02.

| Feed, snapshot | `stop_times` rows | Primary key built | Time | Size in Postgres | of it `stop_times` |
|---|---|---|---|---|---|
| HSL 2026-10-31 14:33 | 10.67 M | during the copy | 128.7 s | 1806 MB | 869 MB table + 799 MB index |
| HSL 2026-10-31 14:33 | 10.67 M | after the copy | 131.3 s | 1507 MB | 869 MB table + 516 MB index |
| Entur (Bergen) 2026-11-01 14:34 | 3.80 M | during the copy | 73.5 s | 1237 MB | — |
| MBTA 2026-10-31 02:32 | 2.89 M | during the copy | 28.4 s | 361 MB | — |

- Building the key after the copy (the code since then) does not change the time but makes the indexes ~35 % smaller:
  one sorted build fills B-tree pages, random inserts leave them about two-thirds full.
- The import is 5–7 times slower than the check alone (HSL ≤ 28 s, above): the time goes into writing rows, not into
  reading the zip or into index maintenance. Where exactly (row writes and WAL, ~100 M per-value writes on the client,
  the container's memory limit) is not measured yet.
- Postgres holds ~25–30 times the zip size: text keys (`trip_id`, `stop_id`) repeat in every `stop_times` row and its
  index. While a feed switches versions both are on disk (HSL ~3 GB today).
- A snapshot whose file is already active is skipped by its SHA-256 without reading the zip; the broken HSL export of
  2026-10-27 02:30 is rejected with the same single problem as the check alone and is not read again by the Worker.
