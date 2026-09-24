namespace CampaignTool.Web;

// One class per section in SPEC.md "Configuration keys".

public class AppOptions
{
    public string TimeZone { get; set; } = "America/Chicago";
    public string MailingAddress { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

public class AuthOptions
{
    public string AllowedUsers { get; set; } = "";
    public string UnsubscribeKey { get; set; } = "";

    public bool IsAllowed(string? upn) =>
        !string.IsNullOrWhiteSpace(upn) &&
        AllowedUsers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(upn.Trim(), StringComparer.OrdinalIgnoreCase);
}

public class AcsOptions
{
    public string ConnectionString { get; set; } = "";
    public string SenderAddress { get; set; } = "";

    /// <summary>Comma-separated MailFrom addresses on every linked domain, set by the Bicep; the owner picks one in Settings.</summary>
    public string SenderAddresses { get; set; } = "";

    public IReadOnlyList<string> AllowedSenders() =>
        SenderAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Prepend(SenderAddress).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsAllowedSender(string? address) =>
        !string.IsNullOrWhiteSpace(address) && AllowedSenders().Contains(address.Trim(), StringComparer.OrdinalIgnoreCase);
}

public class WebhookOptions
{
    public string AcsSecret { get; set; } = "";
}

public class SendingOptions
{
    public int MaxPerMinute { get; set; } = 25;
    public int MaxPerHour { get; set; } = 90;
    public int BatchSize { get; set; } = 25;
}

public class ImportOptions
{
    public bool UpdateExisting { get; set; } = true;
}

public class RetentionOptions
{
    public int EventMonths { get; set; } = 12;
}

public class StorageOptions
{
    public string PublicContainer { get; set; } = "email-assets";
    public string UploadsContainer { get; set; } = "imports";
}
