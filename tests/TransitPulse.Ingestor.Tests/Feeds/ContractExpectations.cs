using System.Text;
using TransitPulse.Domain;

namespace TransitPulse.Ingestor.Tests.Feeds;

/// <summary>
/// Parses a contract fixture line: a <c>PositionMessage</c> JSON value, or <c>null &lt;reason&gt;</c> when the
/// normalizer must drop that vehicle/record and report the named drop reason (e.g. <c>stale</c>, <c>not_measured</c>).
/// </summary>
internal static class ContractExpectations
{
    /// <summary>True when the line is a drop expectation; <paramref name="reason"/> is the metric reason after <c>null</c>.</summary>
    /// <exception cref="FormatException">The line is bare <c>null</c> or <c>null</c> without a non-empty reason.</exception>
    public static bool IsDrop(string line, out string reason)
    {
        const string Prefix = "null ";
        if (line.Equals("null", StringComparison.Ordinal)
            || (line.StartsWith("null", StringComparison.Ordinal)
                && (line.Length == 4 || line[4] == ' ')))
        {
            reason = line.Length > Prefix.Length ? line[Prefix.Length..] : "";
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new FormatException($"Contract drop must be 'null <reason>', got '{line}'.");
            }

            return true;
        }

        reason = "";
        return false;
    }

    /// <summary>Position lines as messages; drop lines are omitted (their reasons are collected separately).</summary>
    public static IEnumerable<VehiclePosition> Positions(IEnumerable<string> lines) =>
        lines.Where(line => !IsDrop(line, out _)).Select(line => PositionMessage.Parse(Encoding.UTF8.GetBytes(line)));

    /// <summary>Drop reasons in fixture order.</summary>
    public static IReadOnlyList<string> DropReasons(IEnumerable<string> lines)
    {
        var reasons = new List<string>();
        foreach (var line in lines)
        {
            if (IsDrop(line, out var reason))
            {
                reasons.Add(reason);
            }
        }

        return reasons;
    }
}
