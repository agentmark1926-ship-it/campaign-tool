using CampaignTool.Web.Data;

namespace CampaignTool.Web.Services;

/// <summary>The campaign state machine and the retry schedule (SPEC "Campaign sending pipeline").</summary>
public static class CampaignRules
{
    private static readonly HashSet<(CampaignStatus, CampaignStatus)> Allowed =
    [
        (CampaignStatus.Draft, CampaignStatus.Scheduled),
        (CampaignStatus.Draft, CampaignStatus.Sending),
        (CampaignStatus.Scheduled, CampaignStatus.Draft),
        (CampaignStatus.Scheduled, CampaignStatus.Sending),
        (CampaignStatus.Sending, CampaignStatus.Paused),
        (CampaignStatus.Paused, CampaignStatus.Sending),
        (CampaignStatus.Sending, CampaignStatus.Completed),
        (CampaignStatus.Sending, CampaignStatus.Cancelled),
        (CampaignStatus.Paused, CampaignStatus.Cancelled),
    ];

    public static bool CanMove(CampaignStatus from, CampaignStatus to) => Allowed.Contains((from, to));

    /// <summary>Changes the status or throws InvalidOperationException for a transition the state machine doesn't allow.</summary>
    public static void Move(Campaign campaign, CampaignStatus to)
    {
        if (!CanMove(campaign.Status, to))
            throw new InvalidOperationException($"A {campaign.Status} campaign can't become {to}.");
        campaign.Status = to;
    }

    /// <summary>
    /// Transient failures are retried after 1 min, 5 min, 30 min, 2 h and 6 h. The first send plus these five retries
    /// is the whole budget: when the fifth retry also fails the row is Failed.
    /// </summary>
    public static readonly TimeSpan[] Backoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)];

    public const int MaxRetries = 5;

    /// <summary>Given how many transient failures the row has had (including this one), when to retry; null means Failed.</summary>
    public static DateTime? NextAttempt(int failuresSoFar, DateTime nowUtc) =>
        failuresSoFar <= MaxRetries ? nowUtc + Backoff[failuresSoFar - 1] : null;

    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(10);
}
