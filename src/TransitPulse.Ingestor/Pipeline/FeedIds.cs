using System.Buffers;

namespace TransitPulse.Ingestor.Pipeline;

/// <summary>Rules for feed identifiers, checked when the pipeline starts and again by the archive.</summary>
internal static class FeedIds
{
    // The feed id becomes a path segment: a restricted alphabet rules out "..", separators and case clashes.
    private static readonly SearchValues<char> _allowed = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-");

    /// <summary>Human-readable form of the rule, for error messages.</summary>
    public const string Rule = "only a-z, 0-9 and '-'";

    /// <summary>Whether <paramref name="feedId"/> is non-empty and uses only lowercase letters, digits and '-'.</summary>
    /// <param name="feedId">Feed identifier to check.</param>
    public static bool IsValid(string feedId) => feedId.Length > 0 && !feedId.AsSpan().ContainsAnyExcept(_allowed);
}
