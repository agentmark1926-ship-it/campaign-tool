using System.Net;
using CampaignTool.Web.Data;
using CampaignTool.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Endpoints;

/// <summary>
/// Public unsubscribe page: GET shows one button, POST (the button, or a mail client's one-click
/// "List-Unsubscribe=One-Click" request) unsubscribes, suppresses the address and counts it on the campaign.
/// Plain server-rendered HTML with no app layout, so it works without sign-in and without the interactive circuit.
/// </summary>
public static class UnsubscribeEndpoint
{
    public static void MapUnsubscribeEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/unsubscribe/{token}", (string token, UnsubscribeTokenService tokens) =>
            tokens.TryValidate(token, out _, out _)
                ? Page("Unsubscribe", $"""
                    <p>Click the button to stop receiving these emails.</p>
                    <form method="post" action="/unsubscribe/{WebUtility.HtmlEncode(token)}">
                      <button type="submit">Unsubscribe</button>
                    </form>
                    """)
                : Invalid());

        app.MapPost("/unsubscribe/{token}", async (string token, UnsubscribeTokenService tokens, UnsubscribeService unsubscribe, CancellationToken ct) =>
        {
            if (!tokens.TryValidate(token, out var contactId, out var campaignId)) return Invalid();
            await unsubscribe.UnsubscribeAsync(contactId, campaignId, ct);
            return Page("You're unsubscribed", "<p>You won't receive these emails any more. Sorry to see you go.</p>");
        }).DisableAntiforgery(); // one-click requests from mail providers carry no antiforgery token; the signed token is the secret
    }

    private static IResult Invalid() => Page("Link not valid",
        "<p>This unsubscribe link is incomplete or has been changed. Use the link from the most recent email, or reply to that email and ask to be removed.</p>",
        StatusCodes.Status400BadRequest);

    private static IResult Page(string title, string body, int status = StatusCodes.Status200OK) => Results.Content($$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/>
        <meta name="robots" content="noindex"/><title>{{title}}</title>
        <style>
          body{font-family:Arial,Helvetica,sans-serif;background:#f5f5f5;color:#222;margin:0;padding:48px 16px}
          main{max-width:480px;margin:0 auto;background:#fff;border-radius:8px;padding:32px;box-shadow:0 1px 3px rgba(0,0,0,.1)}
          h1{font-size:22px;margin:0 0 16px} button{background:#1a4f8b;color:#fff;border:0;border-radius:4px;padding:12px 24px;font-size:16px;cursor:pointer}
        </style></head>
        <body><main><h1>{{title}}</h1>{{body}}</main></body></html>
        """, "text/html; charset=utf-8", statusCode: status);
}
