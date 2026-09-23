using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CampaignTool.Web.Data;
using Scriban;
using Scriban.Runtime;

namespace CampaignTool.Web.Services;

/// <summary>
/// Merge fields with Scriban in Liquid syntax: {{ first_name | default: "there" }}.
/// Every contact value is HTML-encoded in HTML output; unknown fields render empty.
/// Fields: email, first_name, last_name, and each custom field as snake_case (e.g. "Unit Size" → unit_size).
/// </summary>
public static partial class TemplateRenderer
{
    public static readonly string[] BuiltInFields = ["first_name", "last_name", "email"];

    [GeneratedRegex(@"\{\{.*?\}\}", RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonIdentifier();

    public static string FieldName(string customField) => NonIdentifier().Replace(customField.Trim().ToLowerInvariant(), "_").Trim('_');

    public static string RenderHtml(string template, Contact contact) => Render(template, contact, htmlEncode: true);

    public static string RenderText(string template, Contact contact) => Render(template, contact, htmlEncode: false);

    /// <summary>Returns the first template error, or null when the template parses.</summary>
    public static string? Validate(string template)
    {
        var parsed = Scriban.Template.ParseLiquid(DecodeTags(template));
        return parsed.HasErrors ? parsed.Messages[0].Message : null;
    }

    private static string Render(string template, Contact contact, bool htmlEncode)
    {
        var parsed = Scriban.Template.ParseLiquid(DecodeTags(template));
        if (parsed.HasErrors)
            throw new InvalidOperationException($"Merge field error: {parsed.Messages[0].Message}");

        var globals = new ScriptObject();
        string? Value(string? v) => string.IsNullOrEmpty(v) ? null : htmlEncode ? WebUtility.HtmlEncode(v) : v;

        var custom = JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(contact.CustomFields) ? "{}" : contact.CustomFields) ?? [];
        foreach (var (key, value) in custom)
        {
            var name = FieldName(key);
            if (name.Length > 0 && !BuiltInFields.Contains(name)) globals[name] = Value(value);
        }
        globals["first_name"] = Value(contact.FirstName);
        globals["last_name"] = Value(contact.LastName);
        globals["email"] = Value(contact.Email);

        var context = new LiquidTemplateContext { StrictVariables = false, EnableRelaxedMemberAccess = true };
        context.PushGlobal(globals);
        return parsed.Render(context);
    }

    // Visual editors store quotes inside text as &quot;; decode only inside {{ }} so the tag parses.
    private static string DecodeTags(string template) => Tag().Replace(template ?? "", m => WebUtility.HtmlDecode(m.Value));

    /// <summary>Sample contacts for previews: one with a name, one email-only.</summary>
    public static Contact SampleNamed => new() { Email = "ann.lee@example.com", FirstName = "Ann", LastName = "Lee", CustomFields = "{}" };
    public static Contact SampleEmailOnly => new() { Email = "subscriber@example.com", CustomFields = "{}" };
}
