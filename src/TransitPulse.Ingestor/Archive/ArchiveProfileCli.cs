namespace TransitPulse.Ingestor.Archive;

/// <summary>
/// Dispatches <c>--profile</c> and <c>--profile-positions</c>: parses the exclusive UTC day range and runs the
/// matching report, or returns <see cref="ArchiveProfile.UsageExitCode"/> with a usage line on bad dates.
/// </summary>
internal static class ArchiveProfileCli
{
    /// <summary>
    /// Runs a profile command when <paramref name="args"/> match <c>--profile</c> or <c>--profile-positions</c>.
    /// </summary>
    /// <returns>The process exit code, or <see langword="null"/> when the args are not a profile command.</returns>
    public static async Task<int?> TryRunAsync(
        string[] args, TextWriter output, TextWriter errors, CancellationToken cancellationToken = default)
    {
        if (args is ["--profile", var profileInput, var profileFrom, var profileTo])
        {
            if (!ArchiveProfile.TryParseDateRange(profileFrom, profileTo, out var from, out var to, errors))
            {
                return ArchiveProfile.UsageExitCode;
            }

            return await ArchiveProfile.RunAsync(profileInput, from, to, output, cancellationToken);
        }

        if (args is ["--profile-positions", var positionsInput, var positionsFrom, var positionsTo])
        {
            if (!ArchiveProfile.TryParseDateRange(positionsFrom, positionsTo, out var from, out var to, errors))
            {
                return ArchiveProfile.UsageExitCode;
            }

            return await ArchivePositionProfile.RunAsync(positionsInput, from, to, output, cancellationToken);
        }

        return null;
    }
}
