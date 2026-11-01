using System.Globalization;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Archive;

public sealed class ArchiveProfileTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tp-profile-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Hours_go_to_local_weekday_slots_and_hours_without_files_count_as_zero()
    {
        // Monday 26.10, Helsinki UTC+2: 06Z is 08 local. Two files of one hour (a restart) add up.
        await WriteAsync("hsl-20261026T06Z-0", 7200);
        await WriteAsync("hsl-20261026T06Z-1", 3600);

        var (exitCode, rows) = await RunAsync(new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 27));

        Assert.Equal(0, exitCode);
        Assert.Equal(["08", "1", "3.00", "3.00"], rows["08"][..4]);
        Assert.Equal(["09", "1", "0.00", "0.00"], rows["09"][..4]);
        Assert.Equal(["1", "0.00"], rows["12"][1..3]);
        Assert.Equal(["0", "-", "-", "-"], rows["08"][5..]);
    }

    [Fact]
    public async Task The_repeated_local_hour_of_the_dst_change_is_one_more_sample_of_its_slot()
    {
        // Helsinki left summer time on Sunday 25.10 at 01:00Z: both 00Z and 01Z are 03 local.
        await WriteAsync("hsl-20261025T01Z-0", 3600);

        var (_, rows) = await RunAsync(new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 26));

        // Saturday 03 (00Z 24.10), Sunday 03 summer time (00Z 25.10) and Sunday 03 winter time (01Z 25.10).
        Assert.Equal(["3", "0.33", "1.00"], rows["03"][5..8]);
    }

    [Fact]
    public async Task Unreadable_file_is_listed_and_fails_the_run()
    {
        await WriteAsync("hsl-20261026T06Z-0", 1);
        var relative = Path.Combine("mbta-vp", "2026-10-26", "mbta-vp-20261026T06Z-0.parquet");
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "mbta-vp", "2026-10-26"));
        await File.WriteAllBytesAsync(Path.Combine(_dir.FullName, relative), [1, 2, 3, 4], Ct);
        var output = new StringWriter();

        var exitCode = await ArchiveProfile.RunAsync(_dir.FullName, new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 27), output, Ct);

        Assert.Equal(1, exitCode);
        Assert.Contains($"UNREADABLE {relative}", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("hsl (", output.ToString(), StringComparison.Ordinal);
    }

    private async Task WriteAsync(string name, int records)
    {
        var dash = name.LastIndexOf('-');
        var feed = name[..(dash - 13)];
        var hour = DateTimeOffset.ParseExact(name[(dash - 12)..dash], "yyyyMMdd'T'HH'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var directory = Path.Combine(_dir.FullName, feed, hour.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var file = await ArchiveFile.CreateAsync(Path.Combine(directory, name + ".parquet"), rowGroupMaxBytes: 1_000_000, Ct);
        for (var i = 0; i < records; i++)
        {
            await file.AddAsync(new RawRecord(feed, hour.AddMilliseconds(i * 100), [1], null), Ct);
        }

        await file.CompleteAsync(Ct);
    }

    // Table rows of the single feed in the report, keyed by the local hour.
    private async Task<(int ExitCode, Dictionary<string, string[]> Rows)> RunAsync(DateOnly from, DateOnly to)
    {
        var output = new StringWriter();
        var exitCode = await ArchiveProfile.RunAsync(_dir.FullName, from, to, output, Ct);
        var rows = output.ToString().ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(cells => cells.Length == 9 && cells[0].Length == 2 && char.IsAsciiDigit(cells[0][0]))
            .ToDictionary(cells => cells[0]);
        return (exitCode, rows);
    }
}
