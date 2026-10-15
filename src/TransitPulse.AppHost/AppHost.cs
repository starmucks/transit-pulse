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

await builder.Build().RunAsync();
