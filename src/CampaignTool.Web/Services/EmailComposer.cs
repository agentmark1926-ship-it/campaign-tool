using System.Net;
using CampaignTool.Web.Data;

namespace CampaignTool.Web.Services;

/// <summary>
/// Builds one campaign email for one contact: merge fields, hidden preheader, footer with the mailing address and unsubscribe link,
/// and the List-Unsubscribe / one-click headers. Plain-text campaigns get a text part and a simple HTML rendition.
/// </summary>
public static class EmailComposer
{
    public static OutgoingEmail Compose(Campaign campaign, Contact contact, string mailingAddress, string unsubscribeUrl)
    {
        var subject = TemplateRenderer.RenderText(campaign.Subject, contact);
        var headers = new Dictionary<string, string>
        {
            ["List-Unsubscribe"] = $"<{unsubscribeUrl}>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
        };
        var htmlFooter = $"<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:12px;color:#666;padding:24px 0;text-align:center\">"
            + $"{WebUtility.HtmlEncode(mailingAddress)}<br/><a href=\"{WebUtility.HtmlEncode(unsubscribeUrl)}\" style=\"color:#666\">Unsubscribe</a></div>";

        if (campaign.Format == TemplateFormat.Text)
        {
            var text = TemplateRenderer.RenderText(campaign.Text, contact);
            return new OutgoingEmail(contact.Email, subject, TemplateRenderer.TextToHtml(text) + htmlFooter, campaign.ReplyTo,
                PlainText: $"{text}\n\n--\n{mailingAddress}\nUnsubscribe: {unsubscribeUrl}", Headers: headers, From: campaign.FromEmail);
        }

        var html = TemplateRenderer.RenderHtml(campaign.Html, contact);
        var preheader = string.IsNullOrWhiteSpace(campaign.Preheader) ? "" :
            $"<div style=\"display:none;max-height:0;overflow:hidden;opacity:0\">{WebUtility.HtmlEncode(TemplateRenderer.RenderText(campaign.Preheader, contact))}</div>";
        return new OutgoingEmail(contact.Email, subject, InsertAfterBodyTag(html, preheader) is var withPre ? InsertBeforeBodyEnd(withPre, htmlFooter) : html,
            campaign.ReplyTo, Headers: headers, From: campaign.FromEmail);
    }

    private static string InsertAfterBodyTag(string html, string snippet)
    {
        if (snippet.Length == 0) return html;
        var i = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        var close = i < 0 ? -1 : html.IndexOf('>', i);
        return close < 0 ? snippet + html : html.Insert(close + 1, snippet);
    }

    private static string InsertBeforeBodyEnd(string html, string snippet)
    {
        var i = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? html + snippet : html.Insert(i, snippet);
    }
}
