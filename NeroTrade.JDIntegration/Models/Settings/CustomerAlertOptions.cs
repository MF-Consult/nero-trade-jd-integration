namespace NeroTrade.JDIntegration.Models.Settings;

/// <summary>
/// Plain-language error mails to the customer, sent through Resend. Bound from the <c>CustomerAlerts</c>
/// section (env: <c>CustomerAlerts__Enabled</c>, <c>__ResendApiKey</c>, <c>__From</c>, <c>__To</c>).
/// Off by default: the logging pipeline is only wrapped when <see cref="Enabled"/> is true, so with the switch
/// off nothing about the existing logging changes.
/// </summary>
public sealed class CustomerAlertOptions
{
    public const string SectionName = "CustomerAlerts";

    public bool Enabled { get; init; }

    /// <summary>Resend API key. A secret: keep it in app settings, never in the repo.</summary>
    public string? ResendApiKey { get; init; }

    /// <summary>Sender, on a domain verified in Resend, e.g. <c>Nero Trade integration &lt;noreply@mfconsult.dk&gt;</c>.</summary>
    public string? From { get; init; }

    /// <summary>Recipients, comma-separated.</summary>
    public string? To { get; init; }

    /// <summary>The same problem (same code + same order/item) is mailed at most once in this window.</summary>
    public int DedupHours { get; init; } = 24;

    public bool IsComplete => Enabled
        && !string.IsNullOrWhiteSpace(ResendApiKey)
        && !string.IsNullOrWhiteSpace(From)
        && !string.IsNullOrWhiteSpace(To);
}
