using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Services;

public class TemplateService(AppDbContext db, ILogger<TemplateService> log)
{
    public async Task<Template> CreateAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var template = new Template { Name = await UniqueNameAsync("Untitled template", ct), CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Templates.Add(template);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Template {TemplateId} created", template.Id);
        return template;
    }

    /// <summary>Returns an error sentence, or null.</summary>
    public async Task<string?> SaveAsync(int id, string name, TemplateFormat format, string body, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length == 0) return "Give the template a name.";
        if (await db.Templates.AnyAsync(t => t.Name == name && t.Id != id, ct)) return $"Another template is already named '{name}'.";
        if (TemplateRenderer.Validate(body) is { } error) return $"A merge field doesn't parse: {error}";
        var template = await db.Templates.SingleAsync(t => t.Id == id, ct);
        template.Name = name;
        template.Format = format;
        if (format == TemplateFormat.Html) template.Html = body; else template.Text = body;
        template.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<Template> DuplicateAsync(int id, CancellationToken ct = default)
    {
        var source = await db.Templates.AsNoTracking().SingleAsync(t => t.Id == id, ct);
        var now = DateTime.UtcNow;
        var copy = new Template { Name = await UniqueNameAsync($"{source.Name} (copy)", ct), Format = source.Format, Html = source.Html, Text = source.Text, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Templates.Add(copy);
        await db.SaveChangesAsync(ct);
        return copy;
    }

    /// <summary>Campaigns keep their own copy of the design, so deleting a template never changes a campaign.</summary>
    public Task DeleteAsync(int id, CancellationToken ct = default) =>
        db.Templates.Where(t => t.Id == id).ExecuteDeleteAsync(ct);

    private async Task<string> UniqueNameAsync(string baseName, CancellationToken ct)
    {
        var name = baseName;
        for (var i = 2; await db.Templates.AnyAsync(t => t.Name == name, ct); i++) name = $"{baseName} {i}";
        return name;
    }
}
