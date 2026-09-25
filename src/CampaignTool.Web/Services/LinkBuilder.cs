using System.Net;

namespace CampaignTool.Web.Services;

/// <summary>Builds the markup the editor's "Insert link" and "Insert button" place into an email body.</summary>
public static class LinkBuilder
{
    /// <summary>Returns a safe absolute URL (https:// added when missing), or null when the address can't be a link.</summary>
    public static string? Normalize(string? url)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url)) return null;
        if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            return url.Contains(' ') ? null : url;
        if (!url.Contains("://")) url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && u.Host.Contains('.')
            ? u.ToString() : null;
    }

    public static string Link(string text, string url, bool html) =>
        html ? $"<a href=\"{WebUtility.HtmlEncode(url)}\" style=\"color:#0F766E;text-decoration:underline\">{WebUtility.HtmlEncode(Text(text, url))}</a>"
             : $"{Text(text, url)}: {url}";

    /// <summary>A button that renders in Outlook as well as Gmail and Apple Mail (table-based, inline styles).</summary>
    public static string Button(string text, string url, string color = "#0F766E") =>
        $"""
        <table role="presentation" cellspacing="0" cellpadding="0" border="0" style="margin:16px 0"><tr><td style="border-radius:6px;background:{color}">
          <a href="{WebUtility.HtmlEncode(url)}" style="display:inline-block;padding:12px 24px;font-family:Arial,Helvetica,sans-serif;font-size:16px;font-weight:bold;color:#ffffff;text-decoration:none;border-radius:6px">{WebUtility.HtmlEncode(Text(text, url))}</a>
        </td></tr></table>
        """;

    /// <summary>Puts the snippet in place of the selection [start, end), or at the end when there is no cursor position.</summary>
    public static string Insert(string body, string snippet, int start, int end)
    {
        body ??= "";
        if (start < 0 || start > body.Length) start = end = body.Length;
        end = Math.Clamp(end, start, body.Length);
        return body[..start] + snippet + body[end..];
    }

    private static string Text(string text, string url) => string.IsNullOrWhiteSpace(text) ? url : text.Trim();
}
