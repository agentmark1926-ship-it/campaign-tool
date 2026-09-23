using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using CampaignTool.Web.Data;
using Microsoft.Extensions.Options;

namespace CampaignTool.Web.Services;

public class AiOptions
{
    public string AnthropicApiKey { get; set; } = "";
}

/// <summary>
/// Drafts or revises an email body (HTML or plain text) with Claude from the owner's description.
/// The owner always reviews the draft before anything is sent; the footer and unsubscribe link are added at send time.
/// </summary>
public partial class AiEmailWriter(IOptions<AiOptions> options, ILogger<AiEmailWriter> log)
{
    private const string Model = "claude-opus-5";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.AnthropicApiKey);

    private const string SystemPrompt = """
        You write marketing and newsletter emails for a small real-estate company that sends to subscribers who opted in.
        Write the complete email body the user describes. Reply with the email body only: no preamble, no explanation, no code fences.

        Personalisation uses Liquid merge fields. Use them where they read naturally:
        - {{ first_name | default: "there" }} for the greeting (many subscribers have no name on file, so always keep the default)
        - {{ last_name }} and {{ email }} only if the user asks for them

        Do not add an unsubscribe link, a postal address, or a legal footer; the sending system appends those to every email.
        Do not invent facts the user did not give you (prices, dates, addresses, phone numbers, statistics, links). Where one is needed,
        put a clearly marked placeholder in square brackets, such as [MOVE-IN DATE] or [LINK], so the user can fill it in.
        Keep the tone professional and warm, and the copy concise.
        """;

    private const string HtmlRules = """
        Format: a complete HTML email document that renders in Outlook, Gmail and Apple Mail.
        Use a single-column, table-based layout no wider than 600px, inline CSS only (no <style> blocks that the layout depends on,
        no external stylesheets, no web fonts, no JavaScript, no forms). Use web-safe fonts. Buttons are bulletproof table-cell links.
        Only use image URLs the user supplied; otherwise leave images out.
        """;

    private const string TextRules = """
        Format: plain text only. No HTML, no Markdown (no **, #, or bullet asterisks; use simple dashes for lists).
        Keep lines short and separate paragraphs with a blank line.
        """;

    /// <summary>Returns the draft body. Throws InvalidOperationException with a one-sentence reason on failure.</summary>
    public async Task<string> WriteAsync(TemplateFormat format, string instruction, string? currentBody, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("The AI writer isn't set up yet: add your Anthropic API key as the Ai__AnthropicApiKey setting in Azure.");
        if (string.IsNullOrWhiteSpace(instruction))
            throw new InvalidOperationException("Describe the email you want, or the change to make.");

        var request = string.IsNullOrWhiteSpace(currentBody)
            ? $"Write this email:\n\n{instruction.Trim()}"
            : $"Here is the current email:\n\n<current_email>\n{currentBody}\n</current_email>\n\nRevise it as follows, keeping everything else unless the change requires otherwise:\n\n{instruction.Trim()}";

        var client = CreateClient(options.Value.AnthropicApiKey);
        try
        {
            var response = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 16000,
                System = SystemPrompt + "\n\n" + (format == TemplateFormat.Html ? HtmlRules : TextRules),
                Messages = [new() { Role = Role.User, Content = request }],
                // Re-serve on Anthropic's recommended fallback model if a safety classifier declines the request.
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new Default(),
            }, ct);

            if (response.StopReason == BetaStopReason.Refusal)
                throw new InvalidOperationException("Claude declined to write this email; rephrase the request and try again.");

            var text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text)).Trim();
            if (response.StopReason == BetaStopReason.MaxTokens)
                log.LogWarning("AI draft hit the token limit and may be cut off");
            log.LogInformation("AI draft written: {Format}, {InputTokens} in / {OutputTokens} out", format, response.Usage.InputTokens, response.Usage.OutputTokens);
            return StripFences(text);
        }
        catch (AnthropicRateLimitException)
        {
            throw new InvalidOperationException("The AI service is busy; wait a minute and try again.");
        }
        catch (AnthropicApiException ex)
        {
            log.LogWarning("Anthropic API error {Status}", ex.StatusCode);
            throw new InvalidOperationException($"The AI service returned an error ({(int)ex.StatusCode}); check the API key and try again.");
        }
    }

    /// <summary>Tests substitute a fake HTTP handler here.</summary>
    protected virtual AnthropicClient CreateClient(string apiKey) => new() { ApiKey = apiKey };

    [GeneratedRegex(@"^```[a-zA-Z]*\s*\n(.*)\n```\s*$", RegexOptions.Singleline)]
    private static partial Regex Fenced();

    private static string StripFences(string text)
    {
        var m = Fenced().Match(text);
        return m.Success ? m.Groups[1].Value.Trim() : text;
    }
}
