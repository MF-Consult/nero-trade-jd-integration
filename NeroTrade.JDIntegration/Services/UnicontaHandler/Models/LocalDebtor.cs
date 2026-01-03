namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public sealed class LocalDebtor
{
    public string DebtorAccount { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Address1 { get; init; }
    public string? Address2 { get; init; }
    public string? ZipCode { get; init; }
    public string? City { get; init; }
    public string? Country { get; init; }
    public string? CountryCode { get; init; }
}


