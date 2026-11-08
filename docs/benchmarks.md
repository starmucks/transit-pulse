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
- The deduplicator also drops messages older than the vehicle's last position (out of order): 58 045 or 1.05 % of this
  hour, 4.6 % of metro messages; some are copies counted above. `--profile-positions` reports 22.3 % dropped for this
  hour, against a weekday average of 22.5 % for 16:00.
- Metro sends a new fix with a millisecond `tst` and, in between, about one message a second that repeats known
  coordinates with a whole-second `tst`: of 144 006 accepted metro messages, 26 483 bring new coordinates (98 % of them
  with milliseconds) and 117 523 do not. So speed computed between consecutive messages is not meaningful.
- Dropping metro's 7 007 out-of-order messages (95 % with a whole-second `tst`) loses almost nothing: 5 836 repeat the
  last accepted coordinates, 842 earlier ones, 260 reach the stream in a later message, and 69 (0.05 % of the mode's
  messages) are lost.

### HSL vehicle identity

[HSL documents](https://digitransit.fi/en/developers/apis/5-realtime-api/vehicle-positions/high-frequency-positioning/)
that `operator_id/vehicle_number` in the MQTT topic uniquely identifies a vehicle, while the payload's `oper` is the
operator running the trip, which differs under subcontracting. Measured on the same hour with a one-off Python script:

- 1 342 vehicles by topic, 1 337 ids by `oper`/`veh`. Five of those ids each cover two vehicles (`oper` 17, owners 17
  and 20): 32 830 messages, 0.6 %. The two buses behind `17/18` were 8.6–17.1 km apart (median 11.5 km), comparing
  messages at most 1 s apart.
- `oper` differs from the owner in 20.6 % of messages, mostly `oper` 6 on vehicles of owner 18.
- Identifying vehicles by topic adds 202 positions to the hour's 4 272 223 that the deduplicator passes on (0.005 %),
  so the position counts above, measured with `oper`/`veh`, stay valid.

### Plausibility filter

The rule behind `VehicleTrack`: a move is a jump when it is over 200 m and over 60 m/s, measured from the time the
vehicle's current coordinates were first reported. A jump is set aside, and the next position decides whether it was a
glitch or a real move. A one-off Python simulation of the rule over three HSL archive hours, after deduplication, with
vehicles identified by topic. Not yet measured for Bergen and Boston.

| Hour (UTC) | Positions | Dropped | Longest run of drops | Accepted again, p95 |
|---|---:|---:|---:|---:|
| Tue 2026-10-27 14:00 | 4 272 425 | 82 (0.0019 %) | 3 | 2.0 s |
| Fri 2026-10-30 06:00 | 4 175 317 | 122 (0.0029 %) | 2 | 3.4 s |
| Sun 2026-11-01 12:00 | 2 293 584 | 72 (0.0031 %) | 1 | 2.9 s |

- Trains cause 59–80 % of the drops (48, 97 and 56 an hour), at most 0.03 % of train positions.
- No vehicle was still being dropped at the end of an hour. The longest wait for the next accepted position was 37.8 s,
  which includes the vehicle's own silence.
- The distance condition matters: on the first hour, 1 188 consecutive moves exceed 60 m/s, and only 88 of them are over
  200 m. In those 88, the vehicle stayed at the new place 65 times, so a filter that only compared with the last accepted
  point would keep rejecting it.

The real pipeline gives the same result. Replaying 2026-10-27 14:00 UTC through the Ingestor and the Processor on
2026-11-05: 4 272 425 positions, 82 impossible jumps, 0 out of order, 0 unreadable, 4 272 343 published.

### Processor throughput

**How:** one archive hour (2026-10-27 14:00 UTC, HSL) was replayed into separate topics, then a Release build of the
Processor read all of it with a new consumer group. The speed is the growth of its output topic, sampled every 5 s, without
the first and last samples. One process on a development PC, with Redpanda in Docker limited to one core and 1 GB.

| | Result |
|---|---:|
| Speed | **about 115 000 messages/s** (3 619 311 in 31.5 s) |
| CPU | 102 % of one core |
| Peak working set | 215 MiB |

- The C2 target for this topic is 12 000 positions/s, so one loop has about ten times that on this machine. It has not been
  measured on the target server.
- This stage of the Processor only filters and republishes. Matching, stop detection and bunching will take a share of this.

**Live, 2026-11-05 08:53–08:58 UTC, all three cities, Debug build, not the peak:**

| | Result |
|---|---:|
| Input | 869 positions/s |
| Time from `positions.raw` to being read | p50 23 ms, p95 36 ms, p99 44 ms (medians of 30 samples; the highest p95 was 46 ms) |
| Unread messages at the last sample | 0 |
| CPU, working set | 10.7 % of one core, 85 MiB |
| Dropped as impossible jumps | 6 of 260 465 |

The time counts from the moment the Ingestor handed the message to its Kafka client, so it includes that client's batching
(20 ms) and leaves out the source and the Ingestor's own work before that. A run over a weekday peak is task 2.11.

### Archive copy during the morning peak, 2026-11-05

Copying the archive from the collector server with three parallel transfers, started at 05:41 UTC, ran into the Helsinki
morning peak and cost part of the HSL stream. Compared with the same hours a week earlier, read from the archive:

| Hour (UTC) | Records | Points at 1 per 10 s per vehicle |
|---|---:|---:|
| 06:00 | 3 355 855 of 5 591 876 (60 %) | 293 404 of 437 275 (67 %) |
| 07:00 | 3 748 455 of 4 891 318 (77 %) | 296 100 of 370 283 (80 %) |

- Records began arriving late at 06:05 (p95 18 s against 0.5 s), before the first disconnect at 06:16. The rate then fell to
  3 % of normal in the worst 5 minutes (06:50), with 256 s of silence, the longest of the incident.
- Idle CPU on the server fell from about 85 % to 0–5 %, with system time at 72 %. `kcompactd` took a whole core with free
  memory and no reclaim. After the copy stopped at 06:54 the server recovered within minutes, and the rate reached 99 % at
  07:25. A restart of the collector at 07:08 cost another dip (24 % at 07:10).
- Every vehicle stayed visible in every 5-minute window. The stop events feed `hsl-stop` kept 94–97 % of the previous day.
- A separate connection from another machine was also closed by the broker once in 4.5 minutes at about 07:22, so the source
  may have contributed. This cannot be separated.
- Since then: proactive compaction is off; the copy pauses when idle CPU falls below 40 %. The same copy tool then moved
  3.22 GiB in 19:39 during the day (2.8 MiB/s), with 3 pauses and no disconnects.

The window 2026-11-05 06:05–07:30 UTC is left out of model training and evaluation.

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

## HSL stop events feed

HSL's own stop events (`arr`, `ars`, `pde`, `dep`, `pas`, `wait`), collected as feed `hsl-stop` since 2026-11-02
09:10 UTC over a second MQTT connection: the reference for arrival detection (stage 2, task 2.1). The week above ended
before the feed started, so its tables do not include it.

### Volume

**Data:** Tue 2026-11-03 00:00–24:00 UTC, the first full day. **How:** records from the collector's log of closed
archive files, bytes from the file sizes.

| Feed | Records | MiB | Bytes per record |
|---|---:|---:|---:|
| `hsl-stop` | 2 386 848 | 126.9 | 56 |
| `hsl` | 81 246 690 | 2 757.5 | 36 |

- `hsl-stop` adds 2.9 % of the records and 4.6 % of the bytes of `hsl`. All feeds together: 3 474 MiB that day.
- 27.6 events/s on average, 45/s at the peak hour (06:00 UTC), 0.27/s at the lowest (01:00 UTC).
- On 04.11, the hours from 03:00 to 16:00 UTC are within 3 % of the same hours on 03.11.

### Collector memory

**How:** `sar -r` on the collector server, one sample every 10 minutes; the highest "memory used" of each day, without
page cache. The feed has run since 02.11 09:10 UTC; 04.11 covers the day up to 17:30 UTC.

| Days | Highest memory used | Lowest memory available |
|---|---:|---:|
| 27.10–01.11, without `hsl-stop` | 1 053–1 088 MiB | 628–675 MiB |
| 02.11–04.11, with `hsl-stop` | 1 061–1 084 MiB | 646–665 MiB |

- The second connection did not raise the daily peak, including the hours of Boston's evening peak (about 23:00 UTC).
- The process's own memory is a sawtooth, so one reading says little: on 04.11 at about 17:20 UTC, `anon` read 609 and
  653 MiB and `RSS` 666 and 745 MiB within minutes, at the same traffic.
- In the 56 hours after the deploy: no restarts, no out-of-memory kills, 53 s of memory pressure in total.

## Source delay

How old a record is when the collector receives it: receive time minus the time the source gives. This is the floor of
any "source to map" latency, before any of our processing.

**Data:** the archive hour 2026-11-04 16:00 UTC. For the MQTT feeds, 20 000 consecutive messages from the middle of the
hour. **How:** a one-off Python script with pyarrow over the archive files. The server's clock is NTP-synchronized.

| Feed | Source time | Records | p50 | p95 | p99 | Max |
|---|---|---:|---:|---:|---:|---:|
| `hsl` (`vp`) | `tsi` | 20 000 | 0.55 s | 1.16 s | 2.15 s | 19.5 s |
| `hsl-stop` | `tsi` | 20 000 | 0.47 s | 9.53 s | 31.0 s | 4 815 s |
| `entur-sky-vm` | `RecordedAtTime` | 54 991 | 9.22 s | 27.3 s | 32.6 s | 42.3 s |

- `tsi` is in whole seconds, so the HSL figures read up to 1 s high.
- `hsl-stop` events can arrive late (here up to 80 minutes), so comparing with them has to use the event's time, not
  the receive time.
- The `entur-sky-vm` age includes the wait for our next poll, every 30 s. Boston is not measured here.

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

## HSL location source

Where the coordinates of an HFP `VP` message come from (field `loc`), stage 2, task 2.6. Archive hour 2026-11-04 06:00 UTC
(08:00 in Helsinki, weekday peak), 5 579 194 `VP` messages, 5 536 948 of them with coordinates; repeats not removed;
ferries (888 `GPS` positions) left out of the table.

| Mode | `GPS` | `DR` (dead reckoning) | `MAN` | `ODO` |
|---|---:|---:|---:|---:|
| Bus | 3 352 660 | 39 846 | 1 603 | 7 298 |
| Tram | 1 585 869 | 5 188 | 0 | 0 |
| Metro | 0 | 0 | 239 639 | 0 |
| Train | 292 971 | 496 | 0 | 10 490 |
| **Share of positions** | 94.50 % | 0.82 % | 4.36 % | **0.32 %** |

- **`ODO` is the next stop, not the vehicle.** While GPS is lost, the message carries the coordinates of the stop the
  odometer counts towards (`stop` field), unchanged for 10–20 s, then the next stop's. Bus `12/1325` on trip 4736: its `ODO`
  positions ran up to 1.3 km ahead, along the route, of its next GPS fix, under 30 s later; the archive got them about 2 min after
  their `tst`. Kept, they would place a vehicle at a stop it has not reached, push its distance along the route forward,
  and fake an arrival. The Ingestor drops them as `not_measured`.
- Another 39 000 `ODO` messages an hour have no coordinates at all; they count as `not_measured` too, no longer as
  `bad_location`.
- **`MAN` is how the metro reports**, all of it: dropping every location that is not `GPS` would remove the metro. `DR` and
  `MAN` stay; their offsets from the trip's line are measured with route matching (task 2.6).

## Trip lookup

How many HSL positions find their GTFS trip by route, direction, service day and first departure (stage 2, task 2.5).

**How:** a one-off Python simulation of `TripSchedule`, not yet the real pipeline. Every archive hour from 2026-10-21 20:00
to 2026-11-05 07:59 UTC: 354 files, 1 070 420 116 records, every 10th read, duplicates not removed. Each hour uses the
latest snapshot that existed when it started (the broken export of 2026-10-27 skipped; snapshots up to 3 days old), and
the service days are today ± 1 in Helsinki. The base is 106 542 682 sampled positions with a trip descriptor; 505 951
placeholder journeys are outside it.

| Result | Share of positions |
|---|---:|
| Found by `route_id` | 97.556 % |
| Found by route variant (start of `trip_id`) | 1.991 % |
| **Found** | **99.547 %** |
| Route unknown | 0.411 % |
| Ambiguous | 0.036 % |
| Trip exists, its service does not run that day | 0.005 % |
| No such departure | 0.000 % (355) |
| Service day not held | 0.000 % (97) |

- **Worst hour:** 95.50 % (2026-10-31 09:00); none of the 353 files with at least 1 000 sampled positions is below 95 %.
- **Variants are a night effect:** 0.00 % of positions in the 13:00 UTC hour, 24.94 % at 02:00, 11.08 % at 23:00.
- **Snapshot age did not show:** over snapshots up to 3 days old, one journey (`31M1`, 355 positions) had no such
  departure. The change to winter time on 2026-10-25 did not show either (99.79 % that day).
- **Ambiguous** is six routes (`1006`, `1003`, `1010`, `1004`, `1002`, `1008`); most of it is one journey each weekday
  on `1006` and `1003`.

Unknown route codes, none of them in any of the 11 snapshots. The description comes from MQTT topics in one archive hour
per code, except the last row:

| Code | Sampled positions | Seen on | What runs |
|---|---:|---:|---|
| `1009TX` | 162 148 | 15 days | Trams, headsign "Ei linjalla" (not in service), `desi` 000 |
| `1012 8` | 122 420 | 1 day (10-31) | Trams, headsign "KOEAJO" (test run) |
| `1013E` | 72 874 | 7 days | Trams, "Ei linjalla", `desi` 000 |
| `1011 8` | 49 687 | 1 day (10-31) | Trams, "KOEAJO" |
| `6918` | 15 521 | 6 days | One bus, headsign "Hirsala" |
| `6919` | 14 841 | 5 days | Not seen in the hour checked |
| `1012 4`, `7455A` | 615 | 1 day each | Not checked |

- The four tram codes are 93 % of the unknown positions. Without them 99.93 % of positions find their trip.
- These positions correctly stay without a trip, so they produce no stop events or training labels. Their `RouteId` is
  still not a line: anything grouping by line has to use the route of the found trip.

**The real pipeline.** Two hours replayed through a Release build of the Ingestor and the Processor, on separate topics and
a new group, each with its own snapshot in Postgres. Counts come from the metric `transitpulse.processor.trips`.

| Result | Wed 2026-11-04 06:00 UTC | Thu 2026-11-05 00:00 UTC |
|---|---:|---:|
| Accepted positions | 4 309 033 | 181 585 |
| Found | 4 295 429 | 129 727 |
| Route unknown | 6 807 | 0 |
| No departure | 1 038 | 0 |
| Ambiguous | 1 751 | 0 |
| No trip descriptor (parked vehicles) | 4 008 | 51 858 |
| **Found, of positions with a descriptor** | **99.78 %** | **100 %** |

- A whole-hour simulation with the Ingestor's filters gives the same count on every row, except that it also counts the
  positions the Processor drops as impossible jumps (99 and 2).
- These shares are higher than the 99.55 % above because deduplication removes the repeats that trams send.

**Memory and lookup, the same runs:**

| | Result |
|---|---:|
| Runs held, three service days | 71 125 and 71 616 |
| Managed memory of the schedule | at most 14.8 MiB (one reading, includes warming up the database client) |
| One lookup of an ambiguous route, Release | 242 ns |
| Processor idle with the schedule, working set | 76 MiB |
| Processor peak during the replay, working set | 102 and 114 MiB |

Only the first departure of each run was loaded then. Shapes are measured below ("Shapes in the schedule"); stops come
with task 2.7.

**Reading the schedule.** The same query first took 1.3–24.5 s, and one read failed at the default 30 s timeout without a
single row. `EXPLAIN (ANALYZE, BUFFERS)` showed why the time moved: once statistics exist, the planner may look up the first
stop of all 375 292 trips of the snapshot and only then pick the 71 616 wanted. The 30 s itself was not reproduced with the
plan in hand (9.9 s at most in that run), so the plan is the measured cause of the spread, not proof of that one failure.

The query now fixes the order with `MATERIALIZED` and has a 120 s timeout. A new snapshot imported over an old one, then read
through autovacuum and after a manual `ANALYZE`:

| Reads | Failed | Fastest | Median | Slowest |
|---:|---:|---:|---:|---:|
| 16 | 0 | 1.33 s | 2.80 s | 9.78 s |

All on a development PC, Postgres in Docker. A read touches about 40 000 blocks, so a slow disk with a cold cache will take
much longer; this has not been measured on the target server.

### Shapes in the schedule

Task 2.6 loads each run's shape with the schedule: the runs query also returns `shape_id`, and a second query in the same
transaction reads the points of those shapes. HSL snapshot 4, service days 2026-11-07 to 2026-11-09 (Saturday to Monday),
development PC, Postgres in Docker, warm cache.

| | Before (`main`) | With shapes |
|---|---:|---:|
| Runs | 57 010 | 57 010 |
| Runs query, `EXPLAIN ANALYZE`, alternating, 3 runs each | 4.95 s (first, colder), 1.82, 1.50 s | 2.63, 1.50, 1.47 s |
| Runs query plan | | identical, node by node (`EXPLAIN (COSTS OFF)` diff) |
| Shapes / points read | | 982 of 1 233 / 370 162 of 468 449 |
| Points query | | 163–182 ms, primary key index scan, no sort, ~7 000 buffers |
| Whole read through `TripScheduleReader`, Release, after a warm-up | 1.38–1.60 s | 2.06–2.29 s |
| Managed memory held by one schedule | 10.7 MiB | 19.7 MiB |

- **Memory matches the arithmetic:** 370 162 points × 24 bytes (latitude, longitude, distance along, all `double`) is
  8.5 MiB, plus one reference in each run; 9.0 MiB measured. Points take up to 1 355 per shape, 377 on average.
- **`double` stays:** `float` coordinates would save under 3 MiB, at ~0.4 m precision. Not worth a second look while the
  whole schedule is under 20 MiB.
- **The extra 0.6 s is on the client,** not in Postgres (the points query takes 0.17 s): one `shape_id` string per point
  and a copy of each run. A background read once a day; left as is.
- Measured with a throwaway console program holding each loaded schedule, heap measured after a full collection.

## Route matching

How HSL positions are placed on their trip's line by `RouteProgress` (stage 2, task 2.6; ADR-0011).

**How:** a throwaway console program running archive hours through the real `VehicleTrack`, `TripSchedule` and
`RouteProgress`, not yet the whole pipeline. Positions as the Ingestor builds them (`loc` `ODO` dropped, duplicates by
vehicle and time removed), HSL snapshot 4, service days of the hour ± 1. 14 hours chosen to cover the week: Saturday
31.10 10Z and 23Z, Sunday 01.11 12Z and 08.11 12Z, Monday 02.11 00Z, 04Z, 06Z, 10Z, 14Z, 18Z, 21Z, Tuesday 03.11 15Z,
Wednesday 04.11 06Z, Thursday 05.11 14Z. 38 933 758 positions with a found trip and a shape, 25 922 trips.

Two re-anchoring rules compared (thresholds unchanged: off route beyond 50 m, 30 m of noise behind the window):
*any three* — three positions in a row on the line but outside the window place the vehicle anew; *forward* — the
three must also have moved forward along the line by more than 30 m from their rearmost point, and a trip's first
position goes on the first pass of the line within 50 m instead of the nearest one.

| Mode | Positions | On route, any three → forward | Off route | Outside window, forward | Re-anchors back, any three → forward |
|---|---:|---:|---:|---:|---:|
| Bus | 30 379 865 | 99.60 → 99.51 % | 0.40 % | 0.095 % | 1 059 → 333 |
| Tram | 4 132 850 | 99.32 → 98.70 % | 0.66 % | 0.644 % | 286 → 112 |
| Train | 2 767 164 | 89.69 → 89.18 % | 10.25 % | 0.566 % | 660 → 25 |
| Metro | 1 646 203 | 98.92 → 98.69 % | 0.87 % | 0.447 % | 548 → 12 |
| Ferry | 7 676 | 88.87 % | 11.13 % | 0 | 0 |
| **All** | 38 933 758 | **98.83 → 98.65 %** | 1.15 % | 0.201 % | **2 553 → 482** |

Per trip, comparing the two rules:

- **Repeated steps back** (209 trips: 84 buses, 52 metro, 38 trains, 35 trams): with *any three* the distance went
  back again and again, ~100 m each time (30 m of noise + 50 m off route + the fixes of the row). Traced: train `90/6090`
  on `3001R` kept its journey after the run and drove back along the line for 7 min; the distance went back 32 times.
  With *forward* it keeps the distance it reached; the 209 trips have 94 steps back left.
- **A single step back mid-trip** (715 trips: 465 buses, 179 metro): *any three* moved the distance back 45–110 m once;
  *forward* does not. Not traced one by one; the metro is known to repeat old fixes ("HSL repeats").
- **Arriving at the terminal along the line's loop** (350 trips, mostly buses and trams): the first fixes are near the
  loop, 110–280 m along and farther than 50 m from the line's start, so the first pass cannot help. *Forward* places the
  vehicle anew only when it moves off: 127 of them wait over a minute at the first stop. Stop events (task 2.7) will show
  whether first departures are lost.
- **A street the line drives both ways, passes flipping** (20 trips, 0.08 %): the nearest pass changes from fix to fix
  and either rule re-anchors back and forth. Not solved; needs the direction of travel.
- **Forward jumps** (148 trips, mostly metro, +300 to +850 m): positions that jump ahead of the window. Safe for stop
  events (the distance grows), but stops in between may be missed.
- The fall in "on route" is positions that the false steps back used to count as on the line; they are outside the
  window now.
- Trains are 10 % off route under both rules: wide station tracks (offsets of 50–58 m at stations) and journeys kept
  after the run. Left to the off-route threshold per mode (task 2.6, run through the pipeline).
