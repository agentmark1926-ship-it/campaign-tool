namespace CampaignTool.Web.Services;

/// <summary>
/// Sliding-window limiter: never more than the per-minute limit in any 60 seconds or the per-hour limit in any hour.
/// Every send attempt counts (ACS counts requests, not deliveries). Seeded from the database at startup so a restart can't reset the hour.
/// </summary>
public class RateLimiter
{
    private readonly Queue<DateTime> _sends = new();
    private readonly Lock _lock = new();

    public void Seed(IEnumerable<DateTime> recentSendsUtc)
    {
        lock (_lock)
            foreach (var t in recentSendsUtc.Order()) _sends.Enqueue(t);
    }

    public void Record(DateTime nowUtc)
    {
        lock (_lock) _sends.Enqueue(nowUtc);
    }

    /// <summary>How many sends are allowed right now.</summary>
    public int Available(DateTime nowUtc, int perMinute, int perHour)
    {
        lock (_lock)
        {
            Trim(nowUtc);
            var lastMinute = _sends.Count(t => t > nowUtc.AddMinutes(-1));
            return Math.Max(0, Math.Min(perMinute - lastMinute, perHour - _sends.Count));
        }
    }

    /// <summary>When the next send becomes allowed (now if one is available).</summary>
    public DateTime NextAvailable(DateTime nowUtc, int perMinute, int perHour)
    {
        lock (_lock)
        {
            Trim(nowUtc);
            var next = nowUtc;
            var inMinute = _sends.Where(t => t > nowUtc.AddMinutes(-1)).ToList();
            if (inMinute.Count >= perMinute) next = Max(next, inMinute[inMinute.Count - perMinute].AddMinutes(1));
            var inHour = _sends.ToList();
            if (inHour.Count >= perHour) next = Max(next, inHour[inHour.Count - perHour].AddHours(1));
            return next;
        }
    }

    private void Trim(DateTime nowUtc)
    {
        while (_sends.Count > 0 && _sends.Peek() <= nowUtc.AddHours(-1)) _sends.Dequeue();
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
