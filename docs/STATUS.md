# Статус проекта

> Обновляется в конце каждой сессии. Не длиннее одного экрана.

**Обновлено:** 2026-11-27 · **Этап:** 4 · следующая задача **4.1 (#102)**

## Сделано (сессия 2026-11-27)
- **C3 ingest stamp** + ADR-0018 Accepted; **3.9 каркас** `deploy/demo/`; этап 3 закрыт локально; follow-up хоста [#101](https://github.com/starmucks/transit-pulse/issues/101).
- **Этап 4 открыт:** [`docs/stages/stage-4.md`](stages/stage-4.md) · issues #102–#106.
- Закрыты done-issues этапа 3: #79, #81, #84–#87.
- **C3 cut + remasure:** Redis-first, pipelined HashGetAll, LingerMs=0, flush/tick 100 ms → live **p50 402 ms / p95 1.22 s** (n=2000) — цель &lt; 2 s **достигнута**.

## Следующий шаг
1. **4.1 (#102)** — GTFS Bergen/Boston в том же импортёре.
2. [#101](https://github.com/starmucks/transit-pulse/issues/101) публичный хост — когда есть провайдер.

## Известные проблемы
- Сеть хостера сборщика теряет пакеты (инцидент 25.11 ~06Z→…; как 22.11).
- Фаза A: добить оплаченный период и свернуть always-on сбор (без замены VPS) — ADR-0016.
- `StopEventSink` пока не пишет `delay_sec` → CAGG on-time/avg часто нули/`null`.
- C3 p95 &lt; 2 s **достигнута** локально (1.22 s, 2026-11-27); держать consumer groups на tip при cold start Aspire.
- CI: `Replay.Tests` иногда SIGSEGV (exit 139) — flake, rerun; изолирован в workflow.
- Локально `localhost:19092` может уходить в IPv6 (::1) — для Kafka предпочтителен `127.0.0.1`.
- Хук перед коммитом смотрит `$CLAUDE_PROJECT_DIR`, а не worktree.
- До route-keyed repartitioning (ADR-0013) Processor — singleton для слипания.
- Привязка (ADR-0011): двусторонние улицы, скачки вперёд, поезда вне маршрута.
- `PublishAot` выключает EventSource.
- Aspire AppHost: для user-secrets нужен Development / launch profile `https`.
- MapLibre **v6** под Vite — пустая карта; в `web/` зафиксирован **v5**.

## Открытые вопросы
- Нужен ли 4-й город (#106) в этапе 4 или перенос.
- Конкретный pay-per-use провайдер/домен для [#101](https://github.com/starmucks/transit-pulse/issues/101).
- Пороги детектора по видам транспорта; C3 по городам при возрасте Entur.
- C5, исключить окна: 17.10 18–19Z; 20.10 06Z – 21.10 19Z; 05.11 06–07Z; 06.11 18:56 – 08.11 03:38; 09.11 13:40 – 13.11 09:35; 13.11 11:40–19:54 HSL неполный; 22.11 ~12–21Z сеть хостера; **25.11 ~06Z→… сеть хостера (открыто)**.
