namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

public sealed record UpsertResult<T>
{
    public int SuccessCount { get; set; }
    public List<UpsertFailure<T>> Failures { get; } = new();
}

public sealed record UpsertFailure<T>(T Item, int Status, string Message);


