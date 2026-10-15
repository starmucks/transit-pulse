# Этап 0. Фундамент

> **Статус:** утверждена автором 2026-10-09
> **Срок:** 2–3 дня · **Бриф:** §10 «Этап 0» · **Milestone:** `Stage 0 — Foundation`

## Цель

Подготовить репозиторий, процесс и локальную инфраструктуру так, чтобы с этапа 1 каждая задача шла по командному циклу (ветка → PR → зелёный CI → merge), а сбор данных можно было запустить сразу.

**Закрываемые цели брифа:** C1 (свежий .NET 10 код в публичном репо), C9 (CI, тесты), C11 (командный процесс), частично C10 (первые ADR).

## Задачи

Порядок выбран так, чтобы как можно раньше появились CI и hooks: они проверяют все последующие шаги.

### 0.1 Каркас репозитория и solution
- [x] `git init`, первый коммит в `main` — только документы (бриф, CLAUDE.md, docs). Это единственный коммит без PR; дальше всё через PR.
- [x] `LICENSE` (MIT), `.gitignore` (dotnet + node + python), `.gitattributes` (line endings).
- [x] `global.json` (фиксирует SDK 10.0.x), `Directory.Build.props` (net10.0, nullable, `TreatWarningsAsErrors`, анализаторы), `Directory.Packages.props` (Central Package Management), `.editorconfig`.
- [x] `TransitPulse.slnx` + проекты, которые нужны уже сейчас: `TransitPulse.Domain`, `TransitPulse.Domain.Tests` (xUnit), чтобы CI было что собирать и тестировать.
- [x] Короткий `README.md` (что это, статус «в разработке», ссылка на бриф).

**Готово, когда:** `dotnet build` и `dotnet test` зелёные локально, `dotnet format --verify-no-changes` проходит.

### 0.2 GitHub: репозиторий, CI, шаблоны
- [x] Публичный репозиторий на GitHub, push `main`, лейблы, milestones этапов 0–9, issues этапа 0 (#1–#5).
- [x] Защита ветки `main`: merge только через PR с зелёным CI.
- [x] `.github/workflows/ci.yml`: restore → build → test → format check на каждый PR и push в `main` (сделано в PR #6).
- [x] `.github/pull_request_template.md` (что / зачем / как проверить / скриншоты / чек-лист самопроверки), шаблон issue.

**Готово, когда:** первый PR (задача 0.1 или 0.3) прошёл CI и влит через squash-merge.

### 0.3 Инструменты Claude Code (бриф §11.3)
- [x] `.claude/settings.json`: permissions (без подтверждения — `dotnet build/test/format`, `git status/diff/log`), hooks (после правки `.cs` — build; после правки тестов — тесты; перед коммитом — format check).
- [x] Skills: `new-adr`, `start-stage`, `close-stage`; `new-adapter` — заготовка, наполняется в этапе 1.
- [x] Subagents: `reviewer`, `test-writer`.
- [x] `docs/adr/0000-template.md` + ADR-0001 «Record architecture decisions».

**Готово, когда:** правка `.cs` автоматически запускает сборку; `/new-adr` создаёт ADR по шаблону; `reviewer` запускается на тестовом diff.

### 0.4 Локальная инфраструктура на .NET Aspire
- [x] `TransitPulse.AppHost`. `TransitPulse.ServiceDefaults` (OpenTelemetry, health checks, resilience) перенесён в этап 1 вместе с Ingestor — решение автора 2026-10-15 (проекты по мере надобности).
- [x] Ресурсы: Redpanda (Kafka API) + Redpanda Console, PostgreSQL с TimescaleDB и PostGIS, Redis. Volumes для данных, лимиты памяти.
- [x] [ADR-0002](../adr/0002-use-redpanda-instead-of-apache-kafka.md) «Use Redpanda instead of Apache Kafka» (ADR 2 из брифа §8).

**Готово, когда:** `dotnet run --project src/TransitPulse.AppHost` поднимает все контейнеры, в Aspire Dashboard они «Running», к Postgres подключаются расширения `timescaledb` и `postgis`.

### 0.5 Источники данных и VPS (в основном работа автора)
- [ ] Проверить лицензии и условия HSL, Entur, MBTA, Digitraffic; записать в `docs/data-sources.md` (лицензия, атрибуция, обязательные заголовки, лимиты).
- [x] VPS фазы A: у автора есть сервер **1 vCPU / 1 ГБ RAM / 10 ГБ NVMe** (IP в репозиторий не пишем). Диск меньше рекомендованных 25–40 ГБ → окно сырого потока на VPS сокращается до ~1 суток, ежедневная выгрузка на ПК обязательна, алерт по диску с первого дня; способ запуска сборщика (Docker или Native AOT + systemd) — решаем в этапе 1 с учётом диска.
- [ ] Подготовить VPS: SSH-ключ, обновления, firewall — по чек-листу ассистента, выполняет автор.

**Готово, когда:** атрибуция записана; VPS доступен по SSH и готов принять сборщик этапа 1.

## Критерии приёмки этапа (бриф §10)
- [x] `dotnet run` в AppHost поднимает инфраструктуру (Redpanda, Postgres+Timescale+PostGIS, Redis).
- [ ] CI зелёный.
- [ ] Первый PR влит через ревью-процесс (reviewer-сабагент → PR с описанием → CI → squash-merge).
- [ ] Дополнительно: VPS фазы A готов, лицензии проверены — иначе этап 1 не стартует вовремя.

## ADR этапа
- ADR-0001 — Record architecture decisions (формат, процесс).
- [ADR-0002](../adr/0002-use-redpanda-instead-of-apache-kafka.md) — Use Redpanda instead of Apache Kafka (Accepted).
- ADR 1 (модульный монолит) по брифу пишется на этапе 2, но структура закладывается сейчас — можно написать раньше, если решим.

## Решения, принятые в этой спецификации (проверьте)
1. **Проекты создаём по мере надобности**, а не все 8 из §9 сразу: пустые проекты-заглушки ничего не доказывают и засоряют разбор. Ingestor появится в этапе 1, Processor — в этапе 2 и т. д.
2. **Формат solution — `.slnx`** (новый XML-формат, по умолчанию в .NET 10) вместо старого `.sln`.
3. **Central Package Management** — версии пакетов в одном файле `Directory.Packages.props`.
4. Спецификации этапов и `STATUS.md` — на русском (рабочие документы); всё остальное (README, ADR, код) — на английском.

## Риски
| Риск | Мера |
|---|---|
| Этап 0 растягивается (настройка инструментов затягивает) и задерживает сбор данных | Таймбокс 3 дня; skills/subagents — минимальные версии, дорабатываются по ходу |
| VPS не выбран к концу этапа | Решение автора параллельно с 0.1–0.4; в крайнем случае сборщик временно на локальном ПК |
| Образ TimescaleDB+PostGIS тяжёлый | Использовать официальный `timescale/timescaledb-ha` (включает PostGIS); при проблемах — отдельный ADR |
| Нет `gh` CLI | Установить (`winget install GitHub.cli`) или создавать issues/PR через веб |

## Что нужно от автора
- Имя GitHub-аккаунта и репозитория; создать публичный репозиторий.
- Решение по `gh` CLI.
- Выбор и оплата VPS фазы A.

