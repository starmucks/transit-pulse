namespace TransitPulse.Ingestor.Archive;

/// <summary>Raw archive settings, configuration section <c>Archive</c>.</summary>
internal sealed class ArchiveOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Archive";

    /// <summary>Archive root folder; a relative path is resolved against the process working directory.</summary>
    public string RootDirectory { get; set; } = "";

    /// <summary>Buffered payload size that triggers a Parquet row group write; bounds memory per open file.</summary>
    public long RowGroupMaxBytes { get; set; } = 16 * 1024 * 1024;
}
