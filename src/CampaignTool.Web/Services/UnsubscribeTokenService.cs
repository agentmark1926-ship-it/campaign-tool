using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CampaignTool.Web.Services;

/// <summary>Token = base64url("contactId:campaignId") + "." + base64url(HMAC-SHA256 over it with Auth:UnsubscribeKey).</summary>
public class UnsubscribeTokenService(IOptionsMonitor<AuthOptions> auth, IOptionsMonitor<AppOptions> app)
{
    public bool IsConfigured => auth.CurrentValue.UnsubscribeKey.Length >= 16 && !string.IsNullOrWhiteSpace(app.CurrentValue.BaseUrl);

    public string Create(int contactId, int campaignId)
    {
        var payload = Encoding.UTF8.GetBytes($"{contactId}:{campaignId}");
        return $"{Base64Url(payload)}.{Base64Url(Sign(payload))}";
    }

    public string Url(int contactId, int campaignId) => $"{app.CurrentValue.BaseUrl.TrimEnd('/')}/unsubscribe/{Create(contactId, campaignId)}";

    public bool TryValidate(string token, out int contactId, out int campaignId)
    {
        contactId = campaignId = 0;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        try
        {
            var payload = FromBase64Url(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(Sign(payload), FromBase64Url(parts[1]))) return false;
            var ids = Encoding.UTF8.GetString(payload).Split(':');
            return ids.Length == 2 && int.TryParse(ids[0], out contactId) && int.TryParse(ids[1], out campaignId);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private byte[] Sign(byte[] payload) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(auth.CurrentValue.UnsubscribeKey), payload);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
