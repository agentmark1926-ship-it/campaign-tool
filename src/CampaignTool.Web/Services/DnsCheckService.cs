using DnsClient;

namespace CampaignTool.Web.Services;

/// <summary>Resolves the five records a custom ACS sending domain needs (SPEC "Deliverability and DNS") and reports pass/fail for each.</summary>
public class DnsCheckService(ILookupClient dns)
{
    public record Check(string Record, string Host, bool Pass, string Found, string Expected);

    public async Task<IReadOnlyList<Check>> CheckAsync(string domain, CancellationToken ct = default)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        var txt = await TxtAsync(domain, ct);
        var dkim1 = await CnameAsync($"selector1-azurecomm-prod-net._domainkey.{domain}", ct);
        var dkim2 = await CnameAsync($"selector2-azurecomm-prod-net._domainkey.{domain}", ct);
        var dmarc = await TxtAsync($"_dmarc.{domain}", ct);
        var mx = await MxAsync(domain, ct);

        var verification = txt.FirstOrDefault(t => t.StartsWith("ms-domain-verification=", StringComparison.OrdinalIgnoreCase));
        var spf = txt.Where(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();
        var dmarcRecord = dmarc.FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));

        return
        [
            new("Domain verification (TXT)", domain, verification is not null, verification ?? "not found", "ms-domain-verification=… from the Azure portal"),
            new("SPF (TXT)", domain,
                spf.Count == 1 && spf[0].Contains("include:spf.protection.outlook.com", StringComparison.OrdinalIgnoreCase),
                spf.Count switch { 0 => "not found", 1 => spf[0], _ => $"{spf.Count} SPF records (there must be exactly one)" },
                "one record containing include:spf.protection.outlook.com"),
            new("DKIM (CNAME)", $"selector1-azurecomm-prod-net._domainkey.{domain}", dkim1?.EndsWith(".azurecomm.net") == true, dkim1 ?? "not found", "selector1-azurecomm-prod-net._domainkey.azurecomm.net"),
            new("DKIM2 (CNAME)", $"selector2-azurecomm-prod-net._domainkey.{domain}", dkim2?.EndsWith(".azurecomm.net") == true, dkim2 ?? "not found", "selector2-azurecomm-prod-net._domainkey.azurecomm.net"),
            new("DMARC (TXT)", $"_dmarc.{domain}", dmarcRecord is not null, dmarcRecord ?? "not found", "v=DMARC1; p=none (then quarantine after three clean sends)"),
            new("MX", domain, mx.Count > 0, mx.Count > 0 ? string.Join(", ", mx) : "not found", "a mail host you control"),
        ];
    }

    private async Task<List<string>> TxtAsync(string host, CancellationToken ct)
    {
        var result = await dns.QueryAsync(host, QueryType.TXT, cancellationToken: ct);
        return result.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToList();
    }

    private async Task<string?> CnameAsync(string host, CancellationToken ct)
    {
        var result = await dns.QueryAsync(host, QueryType.CNAME, cancellationToken: ct);
        return result.Answers.CnameRecords().Select(r => r.CanonicalName.Value.TrimEnd('.').ToLowerInvariant()).FirstOrDefault();
    }

    private async Task<List<string>> MxAsync(string host, CancellationToken ct)
    {
        var result = await dns.QueryAsync(host, QueryType.MX, cancellationToken: ct);
        return result.Answers.MxRecords().Select(r => r.Exchange.Value.TrimEnd('.')).ToList();
    }
}
