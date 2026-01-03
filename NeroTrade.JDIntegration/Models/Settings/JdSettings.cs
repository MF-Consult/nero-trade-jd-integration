namespace NeroTrade.JDIntegration.Models.Settings;

public sealed class JdSettings
{
    public string? BaseUrl { get; set; }
    public string? BearerToken { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public bool DryRun { get; set; }
}


