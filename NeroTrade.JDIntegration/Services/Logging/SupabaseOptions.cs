namespace NeroTrade.JDIntegration.Services.Logging;

public sealed class SupabaseOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ServiceRoleKey { get; set; } = string.Empty;
    public string IntegrationName { get; set; } = "NeroTrade.JDIntegration";
}
