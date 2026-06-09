namespace NeroTrade.JDIntegration.Services.Logging;

public sealed class SupabaseOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ServiceRoleKey { get; set; } = string.Empty;
    public string IntegrationName { get; set; } = "NeroTrade.JDIntegration";

    /// <summary>
    /// Project column value used to scope the auto-resolve PATCH. Matches the server-side default
    /// (<c>'nero-trade-jd-integration'</c>) so we touch only this integration's rows.
    /// </summary>
    public string Project { get; set; } = "nero-trade-jd-integration";
}
