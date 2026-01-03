namespace NeroTrade.JDIntegration.Models.Settings;

public sealed class UnicontaConfig
{
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public int CompanyId { get; init; }
    public string? BaseUrl { get; init; }
    public int TimeoutSeconds { get; init; } = 30;

    public static UnicontaConfig FromEnvironment()
    {
        return new UnicontaConfig
        {
            Username = Environment.GetEnvironmentVariable("UnicontaConfig__Username") ?? string.Empty,
            Password = Environment.GetEnvironmentVariable("UnicontaConfig__Password") ?? string.Empty,
            ApiKey = Environment.GetEnvironmentVariable("UnicontaConfig__ApiKey") ?? string.Empty,
            CompanyId = int.TryParse(Environment.GetEnvironmentVariable("UnicontaConfig__CompanyId"), out var id) ? id : 0,
            BaseUrl = Environment.GetEnvironmentVariable("UnicontaConfig__BaseUrl"),
            TimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("UnicontaConfig__TimeoutSeconds"), out var t) ? t : 30
        };
    }
}


