using System.Net;

namespace CampaignTool.Web.Services;

/// <summary>Test sends go straight to ACS for at most five addresses and never create recipient rows or count in statistics.</summary>
public class TestEmailService(IEmailSender sender, SettingsService settings, ILogger<TestEmailService> log)
{
    public const int MaxAddresses = 5;

    public record Outcome(string Address, SendResult Result);

    /// <summary>Throws ArgumentException with a one-sentence reason when the request cannot be sent.</summary>
    public async Task<IReadOnlyList<Outcome>> SendAsync(string addresses, string subject, string html, CancellationToken ct = default)
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

        var body = $"{html}<hr/><p style=\"font-size:12px;color:#666\">{WebUtility.HtmlEncode(s.MailingAddress)}<br/>This is a test email; it is not counted in campaign results.</p>";
        var outcomes = new List<Outcome>();
        foreach (var address in list)
        {
            var result = await sender.SendAsync(new OutgoingEmail(address, $"[TEST] {subject}", body, s.ReplyTo), ct);
            outcomes.Add(new Outcome(address, result));
        }
        log.LogInformation("Test email sent: {Accepted} of {Total} accepted by the provider", outcomes.Count(o => o.Result.Success), outcomes.Count);
        return outcomes;
    }
}
