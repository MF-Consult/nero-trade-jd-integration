namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

public sealed record UpsertResult<T>
{
    public int SuccessCount { get; set; }

    /// <summary>
    /// Items that were created (not updated) during the upsert. Lets callers emit per-row
    /// "X created in JD" logs without a second JD round-trip, while keeping noise low — updates
    /// (which fire every sync tick because the source flag stays set) are not surfaced here.
    /// </summary>
    public List<T> CreatedItems { get; } = new();

    public List<UpsertFailure<T>> Failures { get; } = new();

    /// <summary>
    /// JD's request-order id per source (Uniconta) order number, for orders that were created,
    /// recreated, or already existed in JD this run. Lets the caller write JD's id back onto the
    /// Uniconta sales order without a second JD round-trip.
    /// </summary>
    public Dictionary<int, long> JdOrderIdBySourceOrder { get; } = new();
}

public sealed record CreateResult<T>
{
    public int SuccessCount { get; set; }
    public List<T> CreatedItems { get; } = new();
    public List<UpsertFailure<T>> Failures { get; } = new();
}

public sealed record UpsertFailure<T>(T Item, int Status, string Message);


