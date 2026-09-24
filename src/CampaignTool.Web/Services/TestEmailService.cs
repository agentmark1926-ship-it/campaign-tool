using System.Net;
using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Services;

/// <summary>Test sends go straight to ACS for at most five addresses and never create recipient rows or count in statistics.</summary>
public class TestEmailService(IEmailSender sender, SettingsService settings, AppDbContext db, ILogger<TestEmailService> log)
{
    public const int MaxAddresses = 5;

    public record Outcome(string Address, SendResult Result);

    /// <summary>
    /// Merge fields are rendered per address with that contact's data, or as an email-only contact when the address isn't a contact.
    /// Throws ArgumentException with a one-sentence reason when the request cannot be sent.
    /// </summary>
    public async Task<IReadOnlyList<Outcome>> SendAsync(string addresses, string subject, string body, TemplateFormat format = TemplateFormat.Html, CancellationToken ct = default)
    {
        var list = addresses.Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(EmailRules.Normalize).Distinct().ToList();
        if (list.Count == 0) throw new ArgumentException("Enter at least one address.");
        if (list.Count > MaxAddresses) throw new ArgumentException($"A test goes to at most {MaxAddresses} addresses; remove {list.Count - MaxAddresses}.");
        var bad = list.Where(a => !EmailRules.IsValid(a)).ToList();
        if (bad.Count > 0) throw new ArgumentException($"Not a valid address: {string.Join(", ", bad)}.");

        var s = await settings.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(s.MailingAddress))
            throw new ArgumentException("Set the mailing address in Settings first; every email must include it.");

        if (TemplateRenderer.Validate(subject) is { } subjectError) throw new ArgumentException($"The subject's merge field doesn't parse: {subjectError}");
        if (TemplateRenderer.Validate(body) is { } bodyError) throw new ArgumentException($"A merge field doesn't parse: {bodyError}");

        var footer = $"<hr/><p style=\"font-size:12px;color:#666\">{WebUtility.HtmlEncode(s.MailingAddress)}<br/>This is a test email; it is not counted in campaign results.</p>";
        var contacts = await db.Contacts.AsNoTracking().Where(c => list.Contains(c.EmailNormalized)).ToDictionaryAsync(c => c.EmailNormalized, ct);
        var outcomes = new List<Outcome>();
        foreach (var address in list)
        {
            var contact = contacts.GetValueOrDefault(address) ?? new Contact { Email = address, EmailNormalized = address };
            var renderedSubject = $"[TEST] {TemplateRenderer.RenderText(subject, contact)}";
            OutgoingEmail email;
            if (format == TemplateFormat.Text)
            {
                var text = TemplateRenderer.RenderText(body, contact);
                email = new OutgoingEmail(address, renderedSubject, TemplateRenderer.TextToHtml(text) + footer, s.ReplyTo,
                    PlainText: $"{text}\n\n--\n{s.MailingAddress}\nThis is a test email; it is not counted in campaign results.", From: s.SenderAddress);
            }
            else
            {
                email = new OutgoingEmail(address, renderedSubject, TemplateRenderer.RenderHtml(body, contact) + footer, s.ReplyTo, From: s.SenderAddress);
            }
            outcomes.Add(new Outcome(address, await sender.SendAsync(email, ct)));
        }
        log.LogInformation("Test email sent: {Accepted} of {Total} accepted by the provider", outcomes.Count(o => o.Result.Success), outcomes.Count);
        return outcomes;
    }
}
