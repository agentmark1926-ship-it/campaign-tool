using System.Collections.Concurrent;

namespace CampaignTool.Web.Services;

/// <summary>Used when Acs:ConnectionString is empty (local runs, tests). Logs instead of delivering.</summary>
public class InMemoryEmailSender(ILogger<InMemoryEmailSender> log) : IEmailSender
{
    public ConcurrentQueue<(OutgoingEmail Email, string MessageId)> Sent { get; } = new();

    public Task<SendResult> SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString();
        Sent.Enqueue((email, id));
        log.LogInformation("In-memory send {MessageId}: {Subject}", id, email.Subject);
        return Task.FromResult(SendResult.Sent(id));
    }
}
