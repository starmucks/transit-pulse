using System.IO.Compression;

namespace TransitPulse.Integration.Tests.Postgres;

/// <summary>Writes the real HSL tram 9 slice as snapshot zips, optionally edited first.</summary>
internal static class GtfsZips
{
    private static readonly string _tram9 = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gtfs", "hsl-tram-9");

    /// <summary>Zips the slice to <paramref name="path"/>; <paramref name="edit"/> changes file name → content first.</summary>
    public static string Tram9(string path, Action<Dictionary<string, string>>? edit = null)
    {
        var files = Directory.GetFiles(_tram9, "*.txt").ToDictionary(file => Path.GetFileName(file), File.ReadAllText);
        edit?.Invoke(files);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (file, content) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(file).Open());
            writer.Write(content);
        }

        return path;
    }
}
