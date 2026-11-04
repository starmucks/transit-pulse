using System.Text;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Archive;

public sealed class ArchivePositionProfileTests : IDisposable
{
    // Monday 26.10, 06Z = 08 local in Helsinki (UTC+2 after the DST change).
    private static readonly DateTimeOffset _hour = new(2026, 10, 26, 6, 0, 0, TimeSpan.Zero);

    // A real HFP message (tram 2, 2026-10-22) moved into the test hour.
    private const string Payload = """
        {"VP":{"desi":"2","dir":"1","oper":40,"veh":108,"tst":"2026-10-26T06:00:00.952Z","tsi":1792994400,"spd":0.00,
        "hdg":357,"lat":60.167646,"long":24.952242,"acc":0.00,"dl":64,"odo":732,"drst":0,"oday":"2026-10-26","jrn":578,
        "line":30,"start":"07:58","loc":"GPS","stop":1030425,"route":"1002","occu":0}}
        """;

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tp-positions-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Counts_published_positions_and_duplicates_in_the_local_hour_of_receipt()
    {
        // The broker delivering one message twice: the second copy is a duplicate of the same vehicle and time.
        var directory = Path.Combine(_dir.FullName, "hsl", "2026-10-26");
        Directory.CreateDirectory(directory);
        var file = await ArchiveFile.CreateAsync(Path.Combine(directory, "hsl-20261026T06Z-0.parquet"), rowGroupMaxBytes: 1_000_000, Ct);
        const string Topic = "/hfp/v2/journey/ongoing/vp/tram/0040/00108/1002";
        await file.AddAsync(new RawRecord("hsl", _hour.AddSeconds(1), Encoding.UTF8.GetBytes(Payload), Topic), Ct);
        await file.AddAsync(new RawRecord("hsl", _hour.AddSeconds(2), Encoding.UTF8.GetBytes(Payload), Topic), Ct);
        await file.CompleteAsync(Ct);
        var output = new StringWriter();

        var exitCode = await ArchivePositionProfile.RunAsync(_dir.FullName, new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 27), output, Ct);

        Assert.Equal(0, exitCode);
        var report = output.ToString().ReplaceLineEndings("\n").Split('\n');
        var hsl = report.SkipWhile(line => !line.StartsWith("hsl positions", StringComparison.Ordinal)).Skip(2).Take(24)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(cells => cells[0]);
        Assert.Equal(["08", "1", "0.00", "0.00", "50.0"], hsl["08"][..5]);
        Assert.Equal(["09", "1", "0.00", "0.00", "-"], hsl["09"][..5]);
        // Feeds without records in the range still get their table of zeros.
        Assert.Contains(report, line => line.StartsWith("mbta-vp positions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_file_is_reported_instead_of_thrown_and_fails_the_run_without_a_partial_report()
    {
        var directory = Path.Combine(_dir.FullName, "hsl", "2026-10-26");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "hsl-20261026T06Z-0.parquet"), [1, 2, 3, 4], Ct);
        var output = new StringWriter();

        var exitCode = await ArchivePositionProfile.RunAsync(_dir.FullName, new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 27), output, Ct);

        Assert.Equal(1, exitCode);
        var report = output.ToString();
        Assert.StartsWith("UNREADABLE ", report, StringComparison.Ordinal);
        Assert.Contains("hsl-20261026T06Z-0.parquet", report, StringComparison.Ordinal);
        Assert.DoesNotContain(" positions (", report, StringComparison.Ordinal);
    }
}
