namespace NeroTrade.JDIntegration.Services.Logging;

/// <summary>
/// Holds a correlation id shared across every <see cref="IntegrationLogEntry"/> emitted within a single
/// function invocation. Registered as Scoped in DI so each timer/HTTP trigger gets a fresh id.
/// </summary>
public sealed class IntegrationLogScope
{
    public Guid CorrelationId { get; } = Guid.NewGuid();
}
