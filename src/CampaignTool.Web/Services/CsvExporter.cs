using System.Globalization;
using CsvHelper;

namespace CampaignTool.Web.Services;

public static class CsvExporter
{
    /// <summary>Cells starting with =, +, - or @ get a leading apostrophe so spreadsheets don't run them as formulas.</summary>
    public static string Safe(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;

    public static async Task WriteAsync(TextWriter writer, IEnumerable<string> header, IAsyncEnumerable<string?[]> rows, CancellationToken ct = default)
    {
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture, leaveOpen: true);
        foreach (var h in header) csv.WriteField(h);
        await csv.NextRecordAsync();
        await foreach (var row in rows.WithCancellation(ct))
        {
            foreach (var cell in row) csv.WriteField(Safe(cell));
            await csv.NextRecordAsync();
        }
        await csv.FlushAsync();
    }
}
