using System.IO.Compression;
using System.Text;
using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Infrastructure.Tests.Gtfs;

/// <summary>Builds GTFS snapshots in memory from the fixture slice or from inline files.</summary>
internal static class GtfsFixture
{
    private static readonly string _tram9 = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gtfs", "hsl-tram-9");

    /// <summary>File name → content of the real HSL tram 9 slice, to modify per test.</summary>
    public static Dictionary<string, string> Tram9Files() =>
        Directory.GetFiles(_tram9, "*.txt").ToDictionary(path => Path.GetFileName(path), path => File.ReadAllText(path));

    /// <summary>Packs the files into a zip in memory, as the snapshot server would deliver them.</summary>
    public static GtfsFeed Feed(Dictionary<string, string> files)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        buffer.Position = 0;
        return new GtfsFeed(new ZipArchive(buffer, ZipArchiveMode.Read));
    }
}
