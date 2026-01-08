namespace NeroTrade.JDIntegration.Models.Settings;

public class StatusMappingConfig
{
    public Dictionary<int, string> JdStatusToUnicontaGroup { get; set; } = new()
    {
        { 0, "Afventer" }
    };
}

