using Npgsql;

namespace TransitPulse.Integration.Tests.Postgres;

/// <summary>
/// Manual CAGG refresh for tests. Pauses the policy job and retries on Timescale 55P03
/// (concurrent refresh) so parallel Integration.Tests do not flake.
/// </summary>
internal static class ContinuousAggregateRefresh
{
    public static async Task RefreshPunctualityByRouteDayAsync(
        NpgsqlDataSource database,
        CancellationToken cancellationToken)
    {
        await PauseRefreshPoliciesAsync(database, cancellationToken);

        const string sql = "CALL refresh_continuous_aggregate('public.punctuality_by_route_day', NULL, NULL)";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var refresh = database.CreateCommand(sql);
                await refresh.ExecuteNonQueryAsync(cancellationToken);
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable && attempt < 10)
            {
                // 55P03: another refresh (policy or sibling test path) still holds the CAGG.
                await Task.Delay(TimeSpan.FromMilliseconds(40 * attempt), cancellationToken);
            }
        }
    }

    private static async Task PauseRefreshPoliciesAsync(
        NpgsqlDataSource database,
        CancellationToken cancellationToken)
    {
        await using var pause = database.CreateCommand(
            """
            SELECT alter_job(job_id, scheduled => false)
            FROM timescaledb_information.jobs
            WHERE proc_name = 'policy_refresh_continuous_aggregate'
            """);
        await pause.ExecuteNonQueryAsync(cancellationToken);
    }
}
