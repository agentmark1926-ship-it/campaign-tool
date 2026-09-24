namespace CampaignTool.Web.Data;

public enum ContactStatus { Subscribed, Unsubscribed, Bounced, Complained, Invalid }

public enum SuppressionReason { Unsubscribe, HardBounce, Complaint, Manual }

public enum CampaignStatus { Draft, Scheduled, Sending, Paused, Completed, Cancelled }

public enum RecipientStatus { Pending, Claimed, Sent, Delivered, Bounced, Failed, Cancelled, Unknown }

public enum EmailEventKind { Delivery, Engagement }

public enum TemplateFormat { Html, Text }

public enum ImportStatus { Uploaded, Queued, Processing, Completed, Failed }

public class Contact
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string EmailNormalized { get; set; } = "";
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string CustomFields { get; set; } = "{}";
    public ContactStatus Status { get; set; } = ContactStatus.Subscribed;
    public string? StatusReason { get; set; }
    public DateTime StatusChangedAtUtc { get; set; }
    public int SoftBounceCount { get; set; }
    public string Source { get; set; } = "";
    public int? ImportId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Import? Import { get; set; }
    public List<ListContact> ListContacts { get; set; } = [];
}

// Table "Lists"; named ContactList to avoid clashing with System.Collections.Generic.List<T>.
public class ContactList
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public List<ListContact> ListContacts { get; set; } = [];
}

public class ListContact
{
    public int ListId { get; set; }
    public int ContactId { get; set; }
    public DateTime AddedAtUtc { get; set; }

    public ContactList List { get; set; } = null!;
    public Contact Contact { get; set; } = null!;
}

public class Suppression
{
    public string EmailNormalized { get; set; } = "";
    public SuppressionReason Reason { get; set; }
    public string Source { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public class Template
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public TemplateFormat Format { get; set; } = TemplateFormat.Html;
    /// <summary>Unused since the drag-and-drop editor was replaced by HTML / plain-text editing; kept for existing rows.</summary>
    public string DesignJson { get; set; } = "";
    /// <summary>The body when Format is Html.</summary>
    public string Html { get; set; } = "";
    /// <summary>The body when Format is Text.</summary>
    public string Text { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public string Body => Format == TemplateFormat.Html ? Html : Text;
}

public class Campaign
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string? Preheader { get; set; }
    public string FromName { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public string? ReplyTo { get; set; }
    public int ListId { get; set; }
    public string ExcludeListIds { get; set; } = "[]";
    public int? TemplateId { get; set; }
    public TemplateFormat Format { get; set; } = TemplateFormat.Html;
    /// <summary>Unused (the drag-and-drop editor was replaced); kept for the spec's schema.</summary>
    public string DesignJson { get; set; } = "";
    public string Html { get; set; } = "";
    public string Text { get; set; } = "";
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    public DateTime? ScheduledAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int Recipients { get; set; }
    public int Sent { get; set; }
    public int Delivered { get; set; }
    public int Bounced { get; set; }
    public int Failed { get; set; }
    public int Clicked { get; set; }
    public int Unsubscribed { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>When the worker last finished a batch; the "stalled campaign" alert watches it.</summary>
    public DateTime? LastBatchAtUtc { get; set; }

    public Template? Template { get; set; }

    public string Body => Format == TemplateFormat.Html ? Html : Text;
}

public class CampaignRecipient
{
    public long Id { get; set; }
    public int CampaignId { get; set; }
    public int ContactId { get; set; }
    public string EmailSnapshot { get; set; } = "";
    public RecipientStatus Status { get; set; } = RecipientStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
    public string? AcsMessageId { get; set; }
    public string? LastError { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public int ClickCount { get; set; }

    public Campaign Campaign { get; set; } = null!;
    public Contact Contact { get; set; } = null!;
}

public class EmailEvent
{
    public long Id { get; set; }
    public string EventId { get; set; } = "";
    public long? CampaignRecipientId { get; set; }
    public string AcsMessageId { get; set; } = "";
    public EmailEventKind Kind { get; set; }
    public string Status { get; set; } = "";
    public string? Url { get; set; }
    public string? UserAgent { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string RawJson { get; set; } = "";

    public CampaignRecipient? CampaignRecipient { get; set; }
}

public class Import
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public string BlobPath { get; set; } = "";
    public ImportStatus Status { get; set; } = ImportStatus.Uploaded;
    public string Delimiter { get; set; } = ",";
    public bool HasHeader { get; set; }
    /// <summary>JSON array, one entry per file column: "email", "first_name", "last_name", "custom:&lt;name&gt;" or "ignore".</summary>
    public string MappingJson { get; set; } = "[]";
    public int TotalRows { get; set; }
    public int Imported { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Invalid { get; set; }
    public string ErrorsJson { get; set; } = "[]";
    public int? ListId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
