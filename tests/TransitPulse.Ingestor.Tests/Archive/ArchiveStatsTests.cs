using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Archive;

public sealed class ArchiveStatsTests : IDisposable
{
    private static readonly DateTimeOffset _day = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tp-stats-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Reports_records_hours_and_gaps_per_day()
    {
        await WriteAsync("a-20261021T00Z-0", "00:00:00", "00:00:01", "00:00:02");
        await WriteAsync("a-20261021T00Z-1", "00:20:00");
        await WriteAsync("a-20261021T01Z-0", "01:00:30");

        var (exitCode, row) = await RunAsync("a");

        Assert.Equal(0, exitCode);
        Assert.Equal(["a", "2026-10-21", "3", "5"], row[..4]);
        Assert.Equal(["2", "2"], row[5..7]);
        Assert.Equal(["00:40:30", "from", "00:20:00"], row[8..]);
    }

    [Fact]
    public async Task Files_numbered_past_nine_are_read_in_number_order()
    {
        // Ordinal order would read -10 (00:30) before -2 (00:10) and miss the 20-minute gap between them.
        await WriteAsync("a-20261021T00Z-2", "00:10:00");
        await WriteAsync("a-20261021T00Z-10", "00:30:00");

        var (_, row) = await RunAsync("a");

        Assert.Equal("1", row[6]);
        Assert.Equal(["00:20:00", "from", "00:10:00"], row[8..]);
    }

    [Fact]
    public async Task Unreadable_file_is_listed_and_fails_the_run()
    {
        await WriteAsync("a-20261021T00Z-0", "00:00:00");
        var broken = Path.Combine(_dir.FullName, "b", "2026-10-21", "b-20261021T00Z-0.parquet");
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        await File.WriteAllBytesAsync(broken, [1, 2, 3, 4], Ct);
        var output = new StringWriter();

        var exitCode = await ArchiveStats.RunAsync(_dir.FullName, output, Ct);

        Assert.Equal(1, exitCode);
        Assert.Contains($"UNREADABLE {Path.Combine("b", "2026-10-21", "b-20261021T00Z-0.parquet")}", output.ToString());
        Assert.Contains("\na ", output.ToString().ReplaceLineEndings("\n"));
    }

    private async Task WriteAsync(string name, params string[] times)
    {
        var feed = name[..name.IndexOf('-')];
        var directory = Path.Combine(_dir.FullName, feed, "2026-10-21");
        Directory.CreateDirectory(directory);
        var file = await ArchiveFile.CreateAsync(Path.Combine(directory, name + ".parquet"), rowGroupMaxBytes: 1_000_000, Ct);
        foreach (var time in times)
        {
            await file.AddAsync(new RawRecord(feed, _day + TimeSpan.Parse(time, System.Globalization.CultureInfo.InvariantCulture), [1], null), Ct);
        }

        await file.CompleteAsync(Ct);
    }

    private async Task<(int ExitCode, string[] Row)> RunAsync(string feed)
    {
        var output = new StringWriter();
        var exitCode = await ArchiveStats.RunAsync(_dir.FullName, output, Ct);
        var row = output.ToString().ReplaceLineEndings("\n").Split('\n').Single(line => line.StartsWith(feed + " ", StringComparison.Ordinal));
        return (exitCode, row.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
