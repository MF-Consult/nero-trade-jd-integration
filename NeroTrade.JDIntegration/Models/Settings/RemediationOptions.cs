namespace NeroTrade.JDIntegration.Models.Settings;

/// <summary>
/// Settings for the <c>/admin/*</c> remediation endpoints. The shared secret is checked on every
/// call via the <c>X-Remediation-Secret</c> header; if unset, the endpoints refuse to run so the
/// surface cannot accidentally go live without auth.
/// </summary>
public sealed class RemediationOptions
{
    public const string SectionName = "Remediation";

    public string? SharedSecret { get; init; }
}
