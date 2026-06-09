namespace NeroTrade.JDIntegration.Services.Logging;

/// <summary>
/// Holds a correlation id shared across every <see cref="IntegrationLogEntry"/> emitted within a single
/// function invocation. Instantiated manually per invocation — see Program.cs note on why DI scoping is avoided.
/// </summary>
public sealed class IntegrationLogScope
{
    public Guid CorrelationId { get; init; } = Guid.NewGuid();

    /// <summary>Logical run name (e.g. "SyncSalesOrdersToJd"). Surfaced in payload.run_name on every row.</summary>
    public string? RunName { get; init; }
}
