using System.Text.RegularExpressions;

namespace CampaignTool.Web.Services;

/// <summary>Normalization and syntax-only validation (SPEC: no MX or SMTP probing).</summary>
public static partial class EmailRules
{
    [GeneratedRegex(@"<([^<>]+)>")]
    private static partial Regex AngleAddress();

    /// <summary>Trim, strip BOM and surrounding quotes, pull the address out of "Name &lt;addr&gt;", lowercase.</summary>
    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim().Trim('﻿').Trim();
        var angle = AngleAddress().Match(s);
        if (angle.Success) s = angle.Groups[1].Value;
        s = s.Trim().Trim('"', '\'').Trim();
        return s.ToLowerInvariant();
    }

    /// <summary>One @, non-empty local part, a dot in the domain, no spaces, no consecutive or edge dots.</summary>
    public static bool IsValid(string normalized)
    {
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 320) return false;
        if (normalized.Any(char.IsWhiteSpace) || normalized.Contains("..")) return false;
        var parts = normalized.Split('@');
        if (parts.Length != 2) return false;
        var (local, domain) = (parts[0], parts[1]);
        return local.Length > 0 && !local.StartsWith('.') && !local.EndsWith('.')
            && domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }
}
