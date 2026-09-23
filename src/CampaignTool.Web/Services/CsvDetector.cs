using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace CampaignTool.Web.Services;

public record CsvLayout(string Delimiter, bool HasHeader, IReadOnlyList<string> Columns, IReadOnlyList<string[]> SampleRows, int? EmailColumn)
{
    public bool IsSingleColumn => Columns.Count == 1;
}

/// <summary>Delimiter, header and email-column detection per SPEC "Contact import".</summary>
public static class CsvDetector
{
    private static readonly string[] Delimiters = [",", ";", "\t"];
    private const int SampleSize = 200;

    public static CsvConfiguration Config(string delimiter) => new(CultureInfo.InvariantCulture)
    {
        Delimiter = delimiter,
        HasHeaderRecord = false,
        BadDataFound = null,
        MissingFieldFound = null,
        DetectColumnCountChanges = false,
        TrimOptions = TrimOptions.Trim,
        IgnoreBlankLines = true,
    };

    /// <summary>UTF-8 with or without BOM.</summary>
    public static StreamReader Reader(Stream stream) => new(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);

    /// <summary>Yields every non-blank record of the file.</summary>
    public static IEnumerable<string[]> Records(Stream stream, string delimiter)
    {
        using var reader = Reader(stream);
        using var parser = new CsvParser(reader, Config(delimiter));
        while (parser.Read())
        {
            var record = parser.Record ?? [];
            if (record.All(string.IsNullOrWhiteSpace)) continue;
            yield return record;
        }
    }

    public static CsvLayout Detect(Stream stream)
    {
        using var reader = Reader(stream);
        var lines = new List<string>();
        string? line;
        while (lines.Count < SampleSize && (line = reader.ReadLine()) is not null)
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);

        var delimiter = DetectDelimiter(lines);
        var rows = new List<string[]>();
        using (var parser = new CsvParser(new StringReader(string.Join("\n", lines)), Config(delimiter)))
            while (parser.Read()) rows.Add(parser.Record ?? []);

        // Header when the first row has no '@' and the second does (a lone row without '@' is also a header).
        var hasHeader = rows.Count > 0 && !rows[0].Any(v => v.Contains('@')) && (rows.Count == 1 || rows[1].Any(v => v.Contains('@')));
        var columnCount = rows.Count == 0 ? 1 : rows.Max(r => r.Length);
        var columns = Enumerable.Range(0, columnCount)
            .Select(i => hasHeader && i < rows[0].Length && !string.IsNullOrWhiteSpace(rows[0][i]) ? rows[0][i].Trim() : $"Column {i + 1}")
            .ToList();
        var data = hasHeader ? rows.Skip(1).ToList() : rows;

        var emailColumn = FindEmailColumn(data, columnCount) ?? (hasHeader ? FindEmailHeader(columns) : null);
        return new CsvLayout(delimiter, hasHeader, columns, data.Take(5).ToList(), emailColumn);
    }

    private static string DetectDelimiter(List<string> lines)
    {
        // The delimiter that appears the same, non-zero number of times on most lines (quoted text excluded).
        string best = ",";
        var bestScore = 0;
        foreach (var d in Delimiters)
        {
            var counts = lines.Take(20).Select(l => CountOutsideQuotes(l, d[0])).ToList();
            if (counts.Count == 0 || counts.Max() == 0) continue;
            var mode = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).First();
            var score = mode.Key == 0 ? 0 : mode.Count() * 100 + mode.Key;
            if (score > bestScore) (best, bestScore) = (d, score);
        }
        return best;
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var count = 0;
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == delimiter && !quoted) count++;
        }
        return count;
    }

    /// <summary>Fallback when no column is 90% email-shaped (e.g. a small file with a few bad rows): a header such as "Email" or "E-mail Address".</summary>
    private static int? FindEmailHeader(List<string> columns)
    {
        var i = columns.FindIndex(c => c.Replace("-", "").Replace(" ", "").Replace("_", "").ToLowerInvariant() is "email" or "emailaddress" or "mail");
        return i >= 0 ? i : null;
    }

    /// <summary>A one-column file is the email column; otherwise the first column whose values are over 90% email-shaped.</summary>
    private static int? FindEmailColumn(List<string[]> rows, int columnCount)
    {
        if (columnCount == 1) return 0;
        for (var i = 0; i < columnCount; i++)
        {
            var values = rows.Select(r => i < r.Length ? r[i] : "").Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (values.Count > 0 && values.Count(v => EmailRules.IsValid(EmailRules.Normalize(v))) > 0.9 * values.Count)
                return i;
        }
        return null;
    }
}
