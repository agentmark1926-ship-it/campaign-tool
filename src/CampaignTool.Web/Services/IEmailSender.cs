namespace CampaignTool.Web.Services;

public record OutgoingEmail(
    string To,
    string Subject,
    string Html,
    string? ReplyTo = null,
    string? PlainText = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? From = null);

/// <summary>Transient = retry later (429, 5xx, timeout). Not transient and not success = the address was rejected.</summary>
public record SendResult(bool Success, string? MessageId, bool Transient, string? Error)
{
    public static SendResult Sent(string messageId) => new(true, messageId, false, null);
    public static SendResult TransientFailure(string error) => new(false, null, true, error);
    public static SendResult PermanentFailure(string error) => new(false, null, false, error);
}

/// <summary>The one deliberate seam: ACS in Azure, in-memory locally and in tests.</summary>
public interface IEmailSender
{
    Task<SendResult> SendAsync(OutgoingEmail email, CancellationToken ct = default);
}
