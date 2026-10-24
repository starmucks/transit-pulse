# Ingestor — local rules

- **Raw first.** Every message is archived exactly as received before anything else happens; the archive is the
  source of truth. Normalization and publishing are a branch that may drop under backpressure (ADR-0005), never the reverse.
- **Two modes, `Ingestor:Mode`:** `archive` (default; the collector server) and `full` (local, adds Kafka).
  A missing setting must keep meaning `archive`; never add Kafka-dependent code to the archive path.
- **Kafka protocol only** (ADR-0002): `Confluent.Kafka` client features, no Redpanda-specific APIs or admin endpoints.
  Topics are declared in code (`KafkaTopicInitializer`), not left to broker auto-creation.
- **Message key is `{FeedId}/{VehicleId}`** (`PositionMessage.Key`): vehicle ids are unique only within a feed.
- **Native AOT** (ADR-0003): the publish fails on any new trim/AOT warning. Only Parquet.Net and Confluent.Kafka are
  collapsed (see the csproj); Confluent.Kafka stays a `TrimmerRootAssembly`. Check a new library with
  `deploy/collector/publish.sh` before relying on it. Bind configuration with options classes or plain strings, not
  reflection-based enum/type conversion.
- **Native librdkafka:** ship every `librdkafka*.so` variant next to a binary that uses Kafka; the default one needs
  `libsasl2.so.3` (absent on Ubuntu 24.04) and the client falls back to `centos8-librdkafka.so`.
- **SIRI XML only through `XmlReader`** (responses reach several MB; no `XDocument`/`XmlDocument`).
- **Time through `TimeProvider`** (needed by tests and replay); never `DateTime.UtcNow` or `Task.Delay` without it.
- **Single reader.** Sinks, normalizers, the router and the deduplicator are called from one loop and keep state
  without locks; callbacks from the Kafka client run on its own thread and use `Interlocked`.
- **Logs at stream rate are logged per episode** (first failure, then the count on recovery), never per message.
- **Tests:** unit tests in `tests/TransitPulse.Ingestor.Tests` (no Docker); broker tests in
  `tests/TransitPulse.Integration.Tests` (Testcontainers, Docker required). Run like CI:
  `dotnet test --solution TransitPulse.slnx`.
