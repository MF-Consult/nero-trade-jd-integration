namespace NeroTrade.JDIntegration.Services.ExternalIntegration.Repositories;

/// <summary>
/// Thrown by <see cref="IJdRepository"/> read methods when JD returned a non-success status after
/// <see cref="JdRepository.SendWithRetryAsync"/> exhausted its retries. Distinguishing failure from
/// "JD legitimately returned an empty list" matters for <see cref="JdReadCache"/>: an exception
/// prevents the cache from storing a falsely-empty value and silently blocking sync ticks for a
/// full TTL window.
/// </summary>
public sealed class JdLookupFailedException : Exception
{
    public string Endpoint { get; }
    public int StatusCode { get; }
    public string ResponseBody { get; }

    public JdLookupFailedException(string endpoint, int statusCode, string responseBody)
        : base($"JD GET {endpoint} failed with status {statusCode}.")
    {
        Endpoint = endpoint;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
