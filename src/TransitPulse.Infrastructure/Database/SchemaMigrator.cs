using Npgsql;

namespace TransitPulse.Infrastructure.Database;

/// <summary>
/// Applies the SQL migrations embedded in this assembly (<c>Database/Migrations/NNNN_name.sql</c>)
/// in file name order, each exactly once, and records them in <c>public.schema_migrations</c>.
/// </summary>
/// <remarks>
/// Plain SQL instead of EF Core migrations: the schema relies on partitions and TimescaleDB,
/// which EF migrations could only carry as raw SQL anyway.
/// </remarks>
public static class SchemaMigrator
{
    private const string ResourcePrefix = "migrations/";

    // Any constant shared by every process that migrates this database.
    private const long LockKey = 0x5452_414E_5349_5450;

    /// <summary>Applies every migration the database does not have yet.</summary>
    /// <param name="dataSource">Database to migrate.</param>
    /// <param name="cancellationToken">Stops between statements; an interrupted migration rolls back.</param>
    /// <returns>Ids of the migrations applied by this call, in order; empty when the schema was up to date.</returns>
    public static async Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Two services starting together would both see a migration as missing; the second waits here instead.
        await ExecuteAsync(connection, null, $"SELECT pg_advisory_lock({LockKey})", cancellationToken);
        try
        {
            await ExecuteAsync(connection, null,
                "CREATE TABLE IF NOT EXISTS public.schema_migrations (id text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())",
                cancellationToken);

            var applied = await AppliedAsync(connection, cancellationToken);
            var result = new List<string>();
            foreach (var (id, script) in Scripts().Where(migration => !applied.Contains(migration.Id)))
            {
                // DDL is transactional in Postgres: a failing script leaves no half-built schema behind.
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(connection, transaction, script, cancellationToken);
                await using (var record = new NpgsqlCommand("INSERT INTO public.schema_migrations (id) VALUES ($1)", connection, transaction))
                {
                    record.Parameters.Add(new NpgsqlParameter { Value = id });
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                result.Add(id);
            }

            return result;
        }
        finally
        {
            // A session lock outlives Dispose: the pool keeps the physical connection open.
            await ExecuteAsync(connection, null, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
        }
    }

    private static IEnumerable<(string Id, string Script)> Scripts()
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Migration resource {name} is missing"));
            yield return (Path.GetFileNameWithoutExtension(name[ResourcePrefix.Length..]), reader.ReadToEnd());
        }
    }

    private static async Task<HashSet<string>> AppliedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT id FROM public.schema_migrations", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
