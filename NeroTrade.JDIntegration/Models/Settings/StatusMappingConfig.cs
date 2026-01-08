namespace NeroTrade.JDIntegration.Models.Settings;

public class StatusMappingConfig
{
    public Dictionary<int, string> JdStatusToUnicontaGroup { get; set; } = new()
    {
        { 0, "Afventer" },   // Pending
        { 1, "Godkendt" },   // Approved
        { 2, "Afvist" },     // Denied
        { 3, "Annulleret" }  // Cancelled
    };

    public Dictionary<int, string> JdStageToUnicontaGroup { get; set; } = new()
    {
        { 1, "Afvist" },     // Denied
        { 2, "Planlagt" },   // Planned
        { 3, "Pakket" },     // PendingDispatch
        { 4, "Afsendt" }     // Dispatched
    };
}

