using System.Text.Json;
using CampaignTool.Web.Data;
using CampaignTool.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Endpoints;

/// <summary>CSV downloads (behind the sign-in check like every non-public path).</summary>
public static class ExportEndpoints
{
    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/contacts/export.csv", async (HttpContext http, ContactService contacts, string? q, ContactStatus? status, int? listId, CancellationToken ct) =>
        {
            http.Response.ContentType = "text/csv; charset=utf-8";
            http.Response.Headers.ContentDisposition = $"attachment; filename=contacts-{DateTime.UtcNow:yyyyMMdd}.csv";
            var rows = contacts.Query(new ContactQuery(q, status, listId)).OrderBy(c => c.Id)
                .Select(c => new[] { c.Email, c.FirstName, c.LastName, c.Status.ToString(), c.StatusReason, c.CustomFields, c.CreatedAtUtc.ToString("u") })
                .AsAsyncEnumerable();
            await using var writer = new StreamWriter(http.Response.Body);
            await CsvExporter.WriteAsync(writer, ["email", "first_name", "last_name", "status", "status_reason", "custom_fields", "created_utc"], rows, ct);
        });

        app.MapGet("/campaigns/{id:int}/recipients.csv", async (int id, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            if (!await db.Campaigns.AnyAsync(c => c.Id == id, ct)) return Results.NotFound();
            http.Response.ContentType = "text/csv; charset=utf-8";
            http.Response.Headers.ContentDisposition = $"attachment; filename=campaign-{id}-recipients.csv";
            var rows = db.CampaignRecipients.AsNoTracking().Where(r => r.CampaignId == id).OrderBy(r => r.Id)
                .Select(r => new[] { r.EmailSnapshot, r.Status.ToString(), r.SentAtUtc.HasValue ? r.SentAtUtc.Value.ToString("u") : "",
                    r.DeliveredAtUtc.HasValue ? r.DeliveredAtUtc.Value.ToString("u") : "", r.ClickCount.ToString(), r.AttemptCount.ToString(), r.LastError })
                .AsAsyncEnumerable();
            await using var writer = new StreamWriter(http.Response.Body);
            await CsvExporter.WriteAsync(writer, ["email", "status", "sent_utc", "delivered_utc", "clicks", "attempts", "note"], rows, ct);
            return Results.Empty;
        });

        app.MapGet("/imports/{id:int}/rejects.csv", async (int id, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var import = await db.Imports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct);
            if (import is null) return Results.NotFound();
            var rejects = JsonSerializer.Deserialize<List<ImportReject>>(import.ErrorsJson) ?? [];
            http.Response.ContentType = "text/csv; charset=utf-8";
            http.Response.Headers.ContentDisposition = $"attachment; filename=import-{id}-rejects.csv";
            await using var writer = new StreamWriter(http.Response.Body);
            await CsvExporter.WriteAsync(writer, ["row", "value", "reason"],
                rejects.Select(r => new string?[] { r.Row.ToString(), r.Value, r.Reason }).ToAsyncEnumerable(), ct);
            return Results.Empty;
        });
    }
}
