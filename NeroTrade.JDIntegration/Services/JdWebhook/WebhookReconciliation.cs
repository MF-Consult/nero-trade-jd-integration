namespace NeroTrade.JDIntegration.Services.JdWebhook;

/// <summary>
/// Decides whether a change found by a scheduled poll should have been announced by a JD webhook.
/// A poll that finds work while webhooks are configured and none arrived recently means JD did not deliver (or
/// delivered something we dropped) — worth a warning so a silently broken webhook is noticed, not just a slow one.
/// </summary>
public static class WebhookReconciliation
{
    /// <summary>How far back a webhook still counts as "announcing" the change a poll finds.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public static bool IsMissed(bool webhookConfigured, DateTime? lastWebhookUtc, DateTime nowUtc, int changesFoundByPoll)
        => webhookConfigured
           && changesFoundByPoll > 0
           && (lastWebhookUtc is null || nowUtc - lastWebhookUtc.Value > Window);
}
