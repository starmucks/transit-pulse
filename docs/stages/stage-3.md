# Этап 3. Хранение, API, карта — первое демо

> **Статус:** закрыт 2026-11-27 (локальная приёмка 3.1–3.8 + каркас compose) · публичный URL — follow-up · **Бриф:** §10 «Этап 3», §5.1, §6.5–6.6, §6.10 · **Milestone:** `Stage 3 — Storage, API, live map`

## Цель

Показать систему снаружи: живая карта Хельсинки (сначала локально в Aspire), клик по остановке даёт прибытия, линия показывает пунктуальность, задержка «источник → карта» измерена. Это первое демо (C12 начало); публичный compose+HTTPS (C8 начало) — follow-up после локального прогона. Без трёх городов, ML и ассистента.

Уже есть: `positions.enriched`, `events.stop` → `stop_events`, GTFS в Postgres, Processor/Worker в Aspire, офлайн-карта приёмки (`tools/map-spot-check`). Нет: `TransitPulse.Api`, `web/`, записи прореженных позиций, Redis GEO, SignalR.

**Закрываемые цели брифа:** C3 (задержка источник→карта p95 < 2 с — замер на живом пути), C12 начало (демо за 2 минуты, GIF/скрин в README); C8 начало — задача 3.9 после локального демо. Опирается на C4/C9 этапа 2.

## Архитектура этапа (кратко)

```
positions.enriched ──► Worker: PositionSink
                         ├─► Redis GEO (горячие позиции, TTL)
                         └─► Timescale hypertable positions (прореженные 1/10 с; сжатие; retention)
events.stop        ──► Worker: StopEventSink (уже есть) ──► stop_events (→ hypertable + aggregates)
gtfs.* (active)    ──► API (чтение справочника)
Redis GEO + Postgres ──► API REST + SignalR (дельта по bbox / линии, ~1 Гц)
API (+ статика web/) ──► локально Aspire / Vite; позже Caddy HTTPS (3.9)
```

- Полный поток по-прежнему в Parquet (холодный) и в Kafka (горячий буфер). В Postgres — только прореженное и события.
- Сигнал на карту не идёт «всем всё»: клиент подписывается на видимую область или линию; сервер шлёт только дельту видимых машин (ADR этапа, бриф ADR 7).
- Пока фаза A оплачена — сборщик не роняем побочным эффектом разработки; осознанное завершение A — [ADR-0016](../adr/0016-run-the-public-demo-on-demand-with-archive-replay.md).

## Задачи

Порядок: тёплое хранение и GEO → API и SignalR → фронт → замер/README локально. Публичный деплой (3.9) — после локального демо.

### 3.1 Схема тёплого хранения (Timescale) (#79)
- [x] Hypertable прореженных позиций: `(feed_id, vehicle_id, observed_at, …)` + индекс по `(feed_id, route_id, observed_at)`.
- [x] Политика сжатия и retention: позиции — compress 2 дн / retention 14 дн; события — 7 / 90 дн.
- [x] `stop_events` → hypertable PK `(id, at)` + `delay_sec`; CAGG `punctuality_by_route_day`; `StopEventStore` — delete+insert в окне ±2 дн.
- [x] SQL-миграции `0003_warm_storage.sql` через `SchemaMigrator` (ADR-0008).

**Готово, когда:** миграции поднимаются на пустой Timescale в Testcontainers; сжатие/retention видны в каталоге Timescale; aggregate отвечает на простой запрос пунктуальности по фикстурным `stop_events`.
- ✅ 2026-11-22: `WarmStorageTests` + `SchemaMigratorTests` + `StopEventStoreTests` зелёные на `timescaledb-ha:pg17.11-ts2.30.2`.

### 3.2 Приёмник позиций: Timescale + Redis GEO (#80)
- [x] Worker: consumer `positions.enriched` → прореживание (не чаще 1 точки / 10 с на машину, либо последняя в окне) → `COPY`/батч в hypertable + `GEOADD` (и связанные поля: route, bearing, delay…) в Redis с TTL.
- [x] Идемпотентность / безопасный повтор at-least-once (как у `StopEventSink`): повтор не раздувает ряд и не ломает GEO.
- [x] Метрики: записано, пропущено прореживанием, ошибки Redis/Postgres, lag консьюмера.
- [x] ADR многоуровневого хранения и прореживания (бриф ADR 5) — Accepted в этой или соседней задаче.

**Готово, когда:** интеграционный тест пишет прореженный ряд и GEO из фикстурного потока; на живом Aspire после ≥ 10 мин у Хельсинки есть точки в Redis и строки в hypertable; объём/сутки оценён и записан в `docs/benchmarks.md`.
- ✅ 2026-11-23: `PositionSink` + ADR-0015 + `PositionSinkTests` / store tests; оценка объёма в `docs/benchmarks.md`. Live Aspire ≥ 10 мин — ручная приёмка в PR.

### 3.3 Каркас API (#81)
- [x] Проект `TransitPulse.Api` (Minimal APIs, OpenAPI, ServiceDefaults), подключение в AppHost.
- [x] Чтение активного GTFS: маршруты, остановки, shape линии (только то, что нужно карте и странице линии).
- [x] Версионирование путей (`/api/v1/...`); health; без gRPC/Prediction/AI (этапы 6–7).
- [x] `HybridCache` там, где справочник стабилен (маршруты/остановки), L2 = Redis.

**Готово, когда:** локально в Aspire `GET` маршрутов/остановок Хельсинки отвечает по импортированному снимку; OpenAPI открывается; unit/интеграционные тесты на чтение справочника зелёные.
- ✅ 2026-11-25: `GtfsCatalogueReader` (fixed snapshot id for cache) + HybridCache + API DTOs; `GtfsCatalogueReaderTests` 7/7, `GtfsCatalogueCacheTests`; live Aspire — `/health`, `/openapi/v1.json`, `GET …/hsl/routes` (460), `…/stops` (8405), `…/routes/1009/shapes`.

### 3.4 Прибытия и пунктуальность (REST) (#82)
- [x] Остановка → недавние **фактические** события из `stop_events` (не ETA/расписание; default lookback 60 мин).
- [x] Линия → сводка пунктуальности за UTC-день из `punctuality_by_route_day`.
- [x] Контракты ответов стабильны для фронта; неизвестный id → 404; пустое окно → 200 `[]` / нули.

**Готово, когда:** по реальной остановке и линии HSL в Aspire ответы согласованы с данными в Postgres на выборке; тесты на пустую остановку и неизвестный id.
- ✅ 2026-11-26: `StopEventReader` + `PunctualityReader`, index `stop_events_feed_stop_at`; `RealtimeEndpoints`; unit + Postgres tests; Aspire — stop `1113131` events for 2026-11-23, route `1009` punctuality arrivals=50 (`delay_sec` still null from sink → on-time 0).

### 3.5 SignalR: дельты по видимой области (#83)
- [x] Hub: подписка на bbox и/или `routeId`; тик ~1 с; в кадре — только машины в подписке (добавление/обновление/удаление).
- [x] Источник правды для тика — Redis GEO (+ атрибуты); backplane Redis заложен сразу.
- [x] ADR «SignalR: viewport deltas, not broadcast» (бриф ADR 7) → [ADR-0017](../adr/0017-stream-map-vehicles-as-viewport-deltas.md).
- [x] Защита: лимит span bbox 0.5°, max 500 машин/кадр; метрики подписчиков / размера дельты / truncations.

**Готово, когда:** два клиента с разными bbox получают разные множества; нагрузка «весь город широковещанием» не используется; ADR Accepted.
- ✅ 2026-11-26: `VehicleMapHub` + `MapDeltaTicker` + `VehicleViewportReader`; unit + Redis + two-client SignalR tests; ADR-0017 Accepted.

### 3.6 Фронт: живая карта MapLibre (#84)
- [x] `web/`: React + TypeScript + Vite, MapLibre v5 (`react-map-gl`), `@microsoft/signalr`; TanStack Query scaffold (`QueryClientProvider`) for 3.7 REST — no map queries yet.
- [x] Первый экран: карта Хельсинки, машины движутся по SignalR на **живом HSL**; атрибуция источников данных; легенда цветов по задержке.
- [x] Подписка следует за viewport (пан/зум); отписка при уходе (CAS + `IsCurrent` на last-sent; `generation` в delta + фильтр на клиенте).
- [x] Сборка статики под раздачу Caddy/Aspire; не смешивать с `tools/map-spot-check` (тот остаётся офлайн-приёмкой этапа 2).

**Готово, когда:** `dotnet run` AppHost (или documented dev pair API+Vite) показывает движущиеся маркеры на живом HSL; обрыв SignalR переподключается без ручного F5.
- 2026-11-26: локально ~214 машин в viewport на живом HSL (Processor cold start: новая consumer group + `Kafka:AutoOffsetReset=Latest`).

### 3.7 Остановка и страница линии (#85)
- [x] Клик по остановке → панель прибытий из REST 3.4.
- [x] Страница/режим линии: транспорт линии на карте + пунктуальность.
- [x] Состояния загрузки/пусто/ошибка без «тишины».

**Готово, когда:** демо-сценарий «карта → остановка → линия» проходит локально за ≤ 2 минуты без консоли разработчика.
- ✅ 2026-11-26: merged #98; live Aspire — map, route mode, stop panel (empty window when `stop_events` are stale).

### 3.8 Замер задержки и витрина README (локально) (#86)
- [x] Замер «событие в источнике → точка на карте» p50/p95 на **локальном** стеке Aspire + `web/` (методика в `docs/benchmarks.md`; цель C3: p95 < 2 с).
- [x] GIF или скриншот живой карты в `README.md` (только измеренные числа). Пока без обязательной публичной ссылки.
- [x] Укороченный демо-скрипт этапа 3 на локальном URL (полный §12 — к этапу 9): карта, остановка, линия, цифра задержки.

**Готово, когда:** цифры задержки в benchmarks и README; демо «карта → остановка → линия» стабильно воспроизводится локально за ≤ 2 минуты.
- ✅ 2026-11-26: merged #99 (baseline p50 **7.3 s** / p95 **10.1 s**) and #100 (ADR-0018 + segments: total **3.00 / 3.73 s**, to Redis p95 **3.41 s**); screenshot + `docs/demo-stage-3.md`.
- ✅ 2026-11-27: ingest stamp + Redis-first / 100 ms path; live Aspire `?bench=1` total p95 **1.22 s** (C3 met) — `docs/benchmarks.md`.

### 3.9 Публичный деплой (compose + HTTPS) — позже (#87)
- [x] Локальный каркас (2026-11-27): `deploy/demo/` compose + Dockerfile + Caddy + `prepare-golden.ps1` + runbook [`docs/demo-compose.md`](../demo-compose.md); replay `hsl-tram-9-hour` по умолчанию; **не** на VPS сборщика.
- [ ] Форма деплоя зафиксирована 2026-11-25 ([ADR-0016](../adr/0016-run-the-public-demo-on-demand-with-archive-replay.md)): pay-per-use хост, стек **на сеанс**; Ingestor **replay** golden-среза по умолчанию; live-фиды — опциональный профиль; полный архив остаётся на ПК; фаза A не продлевается ради демо.
- [ ] Домен + публичный HTTPS URL на сеанс; registry/образы на хосте; proof billing → 0 — когда автор выберет провайдера.

**Готово, когда:** (после снятия отложения) публичный URL на сеанс; выкатка и гашение по runbook; RAM/диск в бюджете ~4 ГБ без сырого bulk-архива. Каркас compose проверяется локально до выбора хоста.

## Критерии приёмки этапа (бриф §10)

Бриф требует публичную ссылку; **решение автора 2026-11-22:** закрываем этап по локальному демо, публичный URL — follow-up (3.9), не блокер приёмки 3.1–3.8.

- [x] Живая карта локально (Aspire + `web/`): машины движутся, остановка и линия работают.
- [x] Задержка источник→карта измерена и записана (стремимся к p95 < 2 с; если выше — причина и план, без выдуманных цифр).
- [x] Демо показывается за 2 минуты (карта + остановка + линия).
- [x] ADR брифа 5 и 7 приняты (новые номера в `docs/adr/`, не путать с уже существующими ADR-0005/0007 на другие темы).
- [x] README содержит GIF/скрин (публичная ссылка — когда будет 3.9).
- [ ] *(Follow-up / бриф C8)* Публичная ссылка с живой картой — [#101](https://github.com/starmucks/transit-pulse/issues/101) (каркас `deploy/demo/` готов; нужен хост/домен).

## ADR этапа

- Бриф ADR 5 → [ADR-0015](../adr/0015-store-thinned-positions-in-redis-geo-and-timescale.md): многоуровневое хранение и прореживание позиций (горячий Redis GEO, тёплый Timescale, холодный Parquet) — задача 3.2 (#80).
- Бриф ADR 7 → [ADR-0017](../adr/0017-stream-map-vehicles-as-viewport-deltas.md): SignalR viewport/route deltas, not broadcast — задача 3.5 (#83).
- Форма деплоя демо — [ADR-0016](../adr/0016-run-the-public-demo-on-demand-with-archive-replay.md) (Accepted 2026-11-25); локальный каркас — `deploy/demo/` / [#87](https://github.com/starmucks/transit-pulse/issues/87); публичный хост — [#101](https://github.com/starmucks/transit-pulse/issues/101).
- C3 latency path → [ADR-0018](../adr/0018-separate-hot-redis-cadence-from-warm-timescale-thinning.md) (Accepted): Redis-first flush, 100 ms tick; live remasure total p95 **1.22 s** (C3 &lt; 2 s met, 2026-11-27).

## Решения, принятые в этой спецификации (утверждены 2026-11-22; п. 6 уточнён и п. 9 добавлен 2026-11-25)

1. **Продукт этапа 3 — только Хельсинки.** Переключатель городов и обработка Entur/MBTA — этап 4.
2. **Не новый сервис:** приёмник прореженных позиций живёт в **Worker** рядом с `StopEventSink` и GTFS-импортом (ADR-0009).
3. **Схема — SQL-миграции**, чтение API может использовать удобный доступ к Postgres; писатели потока — батч/`COPY`, не построчный EF.
4. **`tools/map-spot-check` не становится продуктом** — `web/` с нуля под живой SignalR.
5. **Приёмка демо §12 урезана:** без Бергена/Бостона, ML vs официальный, инцидентов, Telegram и Grafana. Полный сценарий — этапы 4–8.
6. **Сначала локально:** веб и полный стек в Aspire / dev на ПК; приёмка 3.1–3.8 локальная. Публичный compose+HTTPS (3.9 / #87) — после устойчивого локального демо, по [ADR-0016](../adr/0016-run-the-public-demo-on-demand-with-archive-replay.md) (сеанс, replay по умолчанию).
7. **Прореживание 1/10 с** — стартовое из брифа; порог можно сдвинуть после первого замера объёма.
8. **gRPC, Prediction, AI, rate limit для LLM** — не в этапе 3.
9. **2026-11-25:** фаза A завершается после оплаченного периода сборщика (без замены always-on VPS); история для C5 — локальный архив; публичное демо — pay-per-use хост на сеанс (ADR-0016).

## Риски

| Риск | Мера |
|---|---|
| Локальный ПК не тянет полный Aspire под нагрузкой | Профиль памяти; при нужде — Ingestor/Processor только на HSL; не включать лишнее |
| Широковещание позиций убивает браузер | Только viewport/route дельты (3.5); метрика размера кадра |
| Диск Timescale раздувается | Прореживание + compression + retention с первого дня; оценка ГБ/сутки в 3.2 |
| Публичный деплой дорог или нестабилен | Сеанс + replay (ADR-0016); не держать always-on демо; golden в образе, не bulk-архив |
| Задержка > 2 с | Разложить замер по участкам (источник→Ingestor→Kafka→Processor→Redis→SignalR→клиент); узкое место — follow-up, цифры честные |
| Фронт расползается в «дашборд» | Один фокус: карта + остановка + линия; остальное — следующие этапы |
| Путаница номеров ADR с ADR-0005/0007 | В спецификации и issues всегда писать название темы, не только «ADR 5/7» |
| Приёмка брифа «публичная ссылка» отложена | Явно в критериях; 3.9 не забываем перед заявлением C8 |

## Что нужно от автора

- ~~Утвердить решения 1–8 и issues~~ — 2026-11-22; решение 9 / ADR-0016 — 2026-11-25.
- Конкретный pay-per-use провайдер, домен и тариф — когда снимаем отложение с 3.9 (#87); форма деплоя уже в ADR-0016.

## Новые концепции этапа

TimescaleDB hypertable, compression policy, retention policy, continuous aggregates; Redis GEO; ASP.NET Minimal APIs + OpenAPI; SignalR (hub, backplane, viewport subscription); React + Vite + MapLibre; Caddy automatic HTTPS (3.9); end-to-end latency measurement source→UI.
