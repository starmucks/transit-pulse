using System.Globalization;
using Sylvan.Data.Csv;

namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>
/// Reads one GTFS file row by row without loading it: columns are found by header name, since every feed orders them
/// differently, and typed accessors turn a bad value into a <see cref="GtfsFormatException"/> naming row and column.
/// </summary>
public sealed class GtfsTable : IDisposable
{
    private readonly string _fileName;
    private readonly CsvDataReader _reader;
    private readonly Dictionary<string, int> _columns;

    private GtfsTable(string fileName, CsvDataReader reader)
    {
        _fileName = fileName;
        _reader = reader;
        _columns = new Dictionary<string, int>(reader.FieldCount, StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            _columns.TryAdd(reader.GetName(i), i);
        }
    }

    /// <summary>Opens a GTFS file and checks that its header has every required column.</summary>
    /// <param name="fileName">GTFS file name, used in error messages.</param>
    /// <param name="stream">File content, UTF-8 with or without a byte order mark; disposed with the table, or at once when opening fails.</param>
    /// <param name="requiredColumns">Columns the caller cannot do without.</param>
    /// <returns>The table, positioned before the first row.</returns>
    /// <exception cref="GtfsFormatException">The file has no header row or lacks a required column.</exception>
    public static GtfsTable Open(string fileName, Stream stream, params ReadOnlySpan<string> requiredColumns)
    {
        CsvDataReader reader;
        try
        {
            // Delimiter autodetection would guess from the content; GTFS is always comma-separated.
            reader = CsvDataReader.Create(new StreamReader(stream), new CsvDataReaderOptions { Delimiter = ',' });
        }
        catch (CsvMissingHeadersException)
        {
            stream.Dispose();
            throw new GtfsFormatException(fileName, "the file is empty, without a header row");
        }

        var table = new GtfsTable(fileName, reader);
        try
        {
            foreach (var column in requiredColumns)
            {
                if (!table._columns.ContainsKey(column))
                {
                    throw new GtfsFormatException(fileName, $"required column {column} is missing");
                }
            }

            return table;
        }
        catch
        {
            table.Dispose();
            throw;
        }
    }

    /// <summary>Index of a column checked by <see cref="Open"/>.</summary>
    /// <param name="name">Column name.</param>
    public int Column(string name) => _columns[name];

    /// <summary>Index of a column the file may lack.</summary>
    /// <param name="name">Column name.</param>
    /// <returns>The index, or -1 when the header has no such column; accessors read -1 as an empty field.</returns>
    public int OptionalColumn(string name) => _columns.GetValueOrDefault(name, -1);

    /// <summary>Moves to the next row.</summary>
    /// <returns><see langword="false"/> after the last row.</returns>
    public bool Read() => _reader.Read();

    /// <summary>A field that must not be empty, such as an id.</summary>
    /// <param name="column">Column index.</param>
    public string Text(int column) => OptionalText(column) ?? throw Error(column, "must not be empty");

    /// <summary>A field that may be empty or absent.</summary>
    /// <param name="column">Column index, or -1 for an absent column.</param>
    /// <returns>The text, or <see langword="null"/> when empty or only spaces (HSL writes <c>parent_station</c> as one space).</returns>
    public string? OptionalText(int column) => Field(column).IsEmpty ? null : _reader.GetString(column);

    /// <summary>A required decimal number, such as a coordinate.</summary>
    /// <param name="column">Column index.</param>
    public double Number(int column) => OptionalNumber(column) ?? throw Error(column, "must not be empty");

    /// <summary>A decimal number that may be empty, such as <c>shape_dist_traveled</c>.</summary>
    /// <param name="column">Column index, or -1 for an absent column.</param>
    public double? OptionalNumber(int column) =>
        Field(column) is { IsEmpty: false } text
            ? double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                ? value
                : throw Error(column, "is not a number")
            : null;

    /// <summary>A required integer, such as <c>stop_sequence</c>.</summary>
    /// <param name="column">Column index.</param>
    public int WholeNumber(int column) => OptionalWholeNumber(column) ?? throw Error(column, "must not be empty");

    /// <summary>An integer that may be empty, such as <c>direction_id</c>.</summary>
    /// <param name="column">Column index, or -1 for an absent column.</param>
    public int? OptionalWholeNumber(int column) =>
        Field(column) is { IsEmpty: false } text
            ? int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw Error(column, "is not an integer")
            : null;

    /// <summary>A GTFS time that may be empty: stops between timepoints may have none.</summary>
    /// <param name="column">Column index, or -1 for an absent column.</param>
    public TimeSpan? OptionalTime(int column) =>
        Field(column) is { IsEmpty: false } text
            ? GtfsValues.TryParseTime(text, out var time) ? time : throw Error(column, "is not a GTFS time H:MM:SS")
            : null;

    /// <summary>A required GTFS date.</summary>
    /// <param name="column">Column index.</param>
    public DateOnly Date(int column) =>
        Field(column) is { IsEmpty: false } text
            ? GtfsValues.TryParseDate(text, out var date) ? date : throw Error(column, "is not a GTFS date YYYYMMDD")
            : throw Error(column, "must not be empty");

    /// <inheritdoc />
    public void Dispose() => _reader.Dispose();

    // Spaces around a value break no feed we read, but would fail number and time parsing.
    private ReadOnlySpan<char> Field(int column) => column >= 0 ? _reader.GetFieldSpan(column).Trim() : [];

    /// <summary>An exception for a value of the current row that parses but is not allowed.</summary>
    internal GtfsFormatException Error(int column, string problem) =>
        new(_fileName, $"data row {_reader.RowNumber}, {_reader.GetName(column)} {problem}: '{_reader.GetString(column)}'");
}
