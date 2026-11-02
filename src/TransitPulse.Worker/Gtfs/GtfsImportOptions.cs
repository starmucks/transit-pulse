namespace TransitPulse.Worker.Gtfs;

/// <summary>GTFS import settings, configuration section <c>GtfsImport</c>.</summary>
internal sealed class GtfsImportOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "GtfsImport";

    /// <summary>Snapshot root, <c>{root}/{feed}/{yyyy-MM-dd}T{HHmm}Z.zip</c>; a relative path is resolved against the working directory.</summary>
    public string SnapshotDirectory { get; set; } = "";

    /// <summary>Time between import passes; the first pass runs at start. Snapshots are downloaded once a day.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
}
