using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Options;

namespace CampaignTool.Web.Services;

public class AcsEmailSender(EmailClient client, IOptions<AcsOptions> acs, ILogger<AcsEmailSender> log) : IEmailSender
{
    public async Task<SendResult> SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        var message = new EmailMessage(
            senderAddress: acs.Value.SenderAddress,
            recipientAddress: email.To,
            content: new EmailContent(email.Subject) { Html = email.Html, PlainText = email.PlainText });

        if (!string.IsNullOrWhiteSpace(email.ReplyTo))
            message.ReplyTo.Add(new EmailAddress(email.ReplyTo));
        foreach (var (name, value) in email.Headers ?? new Dictionary<string, string>())
            message.Headers.Add(name, value);

        try
        {
            var operation = await client.SendAsync(WaitUntil.Started, message, ct);
            return SendResult.Sent(operation.Id);
        }
        catch (RequestFailedException ex) when (ex.Status == 429 || ex.Status >= 500)
        {
            log.LogWarning("ACS transient failure {Status} {Code}", ex.Status, ex.ErrorCode);
            return SendResult.TransientFailure($"{ex.Status} {ex.ErrorCode}");
        }
        catch (RequestFailedException ex)
        {
            log.LogWarning("ACS rejected message {Status} {Code}", ex.Status, ex.ErrorCode);
            return SendResult.PermanentFailure($"{ex.Status} {ex.ErrorCode}: {ex.Message}");
        }
        catch (Exception ex) when (ex is TimeoutException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return SendResult.TransientFailure("timeout");
        }
    }
}
