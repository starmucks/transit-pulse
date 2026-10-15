var builder = DistributedApplication.CreateBuilder(args);

// Explicit secret parameters (values live in user secrets): auto-generated passwords were
// regenerated on every run and no longer matched the one stored in the persistent volume.
var postgresPassword = builder.AddParameter("postgres-password", secret: true);
var redisPassword = builder.AddParameter("redis-password", secret: true);

// timescaledb-ha bundles TimescaleDB and PostGIS. The non-OSS build is required:
// compression and continuous aggregates (brief §6.5) are missing from the -oss tags.
var postgres = builder.AddPostgres("postgres", password: postgresPassword, port: 15432)
    .WithImage("timescale/timescaledb-ha", "pg17.11-ts2.30.2")
    // This image keeps its data under /home/postgres/pgdata, not /var/lib/postgresql/data,
    // so WithDataVolume() would mount the wrong path.
    .WithVolume("transit-pulse-postgres", "/home/postgres/pgdata")
    .WithInitFiles("init")
    .WithContainerRuntimeArgs("--memory=1g")
    .WithLifetime(ContainerLifetime.Persistent);

postgres.AddDatabase("transitpulse");

builder.AddRedis("redis", port: 16379, password: redisPassword)
    .WithImageTag("8.8.3-alpine")
    .WithDataVolume("transit-pulse-redis")
    .WithContainerRuntimeArgs("--memory=256m")
    .WithLifetime(ContainerLifetime.Persistent);

// No Aspire integration exists for Redpanda, and AddKafka() expects the Confluent image's
// configuration, so Redpanda is a plain container with explicit start arguments.
// Two listeners: "internal" for containers on the Aspire network, "external" for processes on the host.
var redpanda = builder.AddContainer("redpanda", "redpandadata/redpanda", "v26.2.4")
    .WithArgs(
        "redpanda", "start",
        // dev-container disables fsync and is local only; production keeps --smp/--memory but not this mode.
        "--mode", "dev-container",
        // Same limits as on the production VPS (brief §6.10).
        "--smp", "1", "--memory", "1G",
        "--kafka-addr", "internal://0.0.0.0:9092,external://0.0.0.0:19092",
        "--advertise-kafka-addr", "internal://redpanda:9092,external://localhost:19092")
    .WithEndpoint(name: "kafka", port: 19092, targetPort: 19092, scheme: "tcp")
    .WithHttpEndpoint(name: "admin", port: 19644, targetPort: 9644)
    .WithHttpHealthCheck("/v1/status/ready", endpointName: "admin")
    .WithVolume("transit-pulse-redpanda", "/var/lib/redpanda/data")
    // --memory 1G is Redpanda's own allocation; the container needs headroom above it.
    .WithContainerRuntimeArgs("--memory=1536m")
    .WithLifetime(ContainerLifetime.Persistent);

// Persistent like Redpanda: persistent and session containers live on different Docker networks.
builder.AddContainer("redpanda-console", "redpandadata/console", "v3.12.0")
    .WithEnvironment("KAFKA_BROKERS", "redpanda:9092")
    .WithEnvironment("REDPANDA_ADMINAPI_ENABLED", "true")
    .WithEnvironment("REDPANDA_ADMINAPI_URLS", "http://redpanda:9644")
    .WithHttpEndpoint(port: 18080, targetPort: 8080)
    .WithContainerRuntimeArgs("--memory=256m")
    .WithLifetime(ContainerLifetime.Persistent)
    .WaitFor(redpanda);

await builder.Build().RunAsync();
