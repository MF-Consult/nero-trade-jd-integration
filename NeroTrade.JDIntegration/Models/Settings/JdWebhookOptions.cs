namespace NeroTrade.JDIntegration.Models.Settings;

/// <summary>
/// Settings for the inbound JD webhook endpoint (<c>POST /api/jd/webhook</c>). Bound from the
/// <c>JdWebhook</c> config section (env: <c>JdWebhook__Secret</c>).
/// </summary>
public sealed class JdWebhookOptions
{
    public const string SectionName = "JdWebhook";

    /// <summary>
    /// The <c>secret</c> JD returns when the webhook is created (<c>POST /api/webhooks</c>). JD signs every call
    /// with it: <c>X-JD-Hash</c> = HMAC-SHA256 of <c>X-JD-Job-Id</c>. While empty the endpoint rejects everything
    /// (fail closed), so deploying the code without registering a webhook exposes nothing.
    /// </summary>
    public string? Secret { get; init; }
}
