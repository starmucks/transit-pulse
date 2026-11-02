namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>A GTFS file breaks the format: missing file, header or column, or a value that does not parse.</summary>
/// <remarks>One broken row makes the whole snapshot suspect, so reading stops at the first one.</remarks>
public sealed class GtfsFormatException : Exception
{
    /// <summary>Creates the exception for a problem in one file.</summary>
    /// <param name="fileName">GTFS file name, e.g. <c>stop_times.txt</c>.</param>
    /// <param name="message">What is wrong, including the row and column where known.</param>
    public GtfsFormatException(string fileName, string message)
        : base($"{fileName}: {message}") => FileName = fileName;

    /// <summary>GTFS file the problem was found in.</summary>
    public string FileName { get; }
}
