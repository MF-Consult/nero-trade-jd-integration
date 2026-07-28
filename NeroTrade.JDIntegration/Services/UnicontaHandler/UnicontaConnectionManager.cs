using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.Scheduling;

namespace NeroTrade.JDIntegration.Services.UnicontaHandler;

using Microsoft.Extensions.Logging;
using Uniconta.API.Service;
using Uniconta.API.System;
using Uniconta.Common;
using Uniconta.DataModel;
using Uniconta.Common.User;

public sealed class UnicontaConnectionManager : IDisposable
{
    private readonly ILogger<UnicontaConnectionManager> _logger;
    private readonly UnicontaConfig _config;
    private readonly SyncScheduler _scheduler;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    // One per .NET process. Two managers reporting different ids on the same host means the platform is
    // running several worker processes, each with its own Uniconta session — see ConnectAsync.
    private static readonly Guid ProcessInstanceId = Guid.NewGuid();
    private UnicontaConnection? _connection;
    private Session? _session;
    private Company? _company;
    private bool _isLoggedIn;
    private bool _disposed;
    private DateTime _connectedAtUtc;

    // The connection manager is a singleton, so a session can live for the lifetime of the worker
    // process. Two reasons to recycle aggressively:
    //  1. Uniconta sessions time out server-side after a period of inactivity (and can also be
    //     dropped by nightly server restarts), so we need a periodic forced reconnect anyway.
    //  2. Empirically (2026-05-27, ordre 2161): we observe a ~30 min cycle where queries within a
    //     long-lived session return progressively staler views of the Uniconta data — order updates
    //     done in the Uniconta UI weren't visible to our `Query<DebtorOrderClient>(null)` calls
    //     until the next reconnect. SDK decompile confirmed our code path always hits the server,
    //     so the staleness is per-session server-side. A short age caps customer-visible delay.
    // The max age is now day/night-aware via SyncScheduler (config: SyncScheduling.SessionMaxAge*):
    //  - Day (~150 s): the short cap above matters because users are editing in the Uniconta UI. Raised
    //    from 90 s on 2026-07-28 — each recycle costs two Uniconta calls (Login + OpenCompany), and those
    //    handshakes measured ~56% of the whole daily call budget.
    //  - Night (~15 min): no UI edits happen, so the staleness concern does not apply and we relax
    //    the age to cut reconnects (each reconnect = one Login + one OpenCompany round-trip).
    // Cost during the day: ~40 reconnects/hour per worker instance. Login+OpenCompany is ~500-1000 ms,
    // so the occasional sync tick that hits a reconnect goes from ~25 ms to ~1 s — still well under
    // the timer interval.

    public UnicontaConnectionManager(ILogger<UnicontaConnectionManager> logger, UnicontaConfig config, SyncScheduler scheduler)
    {
        _logger = logger;
        _config = config;
        _scheduler = scheduler;
    }

    public async Task<Session> GetSessionAsync()
    {
        await EnsureConnectedAsync();
        return _session!;
    }

    public async Task<Company> GetCompanyAsync()
    {
        await EnsureConnectedAsync();
        return _company!;
    }

    public bool IsConnected => IsHealthy;

    private bool IsHealthy => _isLoggedIn && _session != null && _company != null;
    private bool IsFresh => DateTime.UtcNow - _connectedAtUtc <= _scheduler.GetSessionMaxAge(DateTime.UtcNow);

    private async Task EnsureConnectedAsync()
    {
        if (IsHealthy && IsFresh)
            return;

        // Serialize (re)connects. Several timer-triggered functions can fire at the same time, and
        // concurrent LoginAsync / OpenCompany calls on the shared session/company corrupt the
        // connection state (observed: cold-start invocations all failing with "Failed to get company").
        await _connectGate.WaitAsync();
        try
        {
            if (IsHealthy && IsFresh)
                return;

            if (IsHealthy)
            {
                _logger.LogInformation("Uniconta session is older than {Age}, reconnecting proactively", _scheduler.GetSessionMaxAge(DateTime.UtcNow));
                ResetConnection();
            }

            await ConnectAsync();
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>
    /// Runs an operation against Uniconta, reconnecting and retrying once if the failure looks like
    /// an expired/invalid session. Use this around mutating calls so a stale session does not lose work.
    /// </summary>
    public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, int maxRetries = 1)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await EnsureConnectedAsync();
                return await operation();
            }
            catch (Exception ex) when (attempt < maxRetries && IsAuthFailure(ex))
            {
                _logger.LogWarning(ex, "Uniconta call failed in a way that looks like an expired session; reconnecting (attempt {Attempt}/{Max})", attempt + 1, maxRetries);
                await ReconnectAsync();
            }
        }
    }

    public Task ExecuteWithRetryAsync(Func<Task> operation, int maxRetries = 1)
        => ExecuteWithRetryAsync(async () =>
        {
            await operation();
            return true;
        }, maxRetries);

    private static bool IsAuthFailure(Exception ex)
    {
        // Uniconta does not surface a single, documented "session expired" exception type. Start broad
        // (message-based, plus NullReferenceException from internal session state) and tighten this once
        // App Insights shows what is actually thrown in production.
        var message = ex.Message ?? string.Empty;
        return ex is NullReferenceException
            || message.Contains("session", StringComparison.OrdinalIgnoreCase)
            || message.Contains("login", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not logged", StringComparison.OrdinalIgnoreCase);
    }

    public UnicontaConnection GetConnection()
    {
        if (_connection == null)
            _connection = new UnicontaConnection(APITarget.Live);
        return _connection;
    }

    /// <summary>
    /// Bounds the wait on a Uniconta SDK call. The SDK accepts no <see cref="CancellationToken"/> and sets
    /// no timeout of its own, so a call against a dead socket never returns — that is what produced the
    /// 30-minute hung invocations (14 in the 30 days to 2026-07-27), each of which blocked its sync's timer
    /// for the full half hour and ended in a forced worker restart.
    ///
    /// We cannot abort the SDK call, only stop waiting on it: on timeout the connection is invalidated so
    /// the next tick reconnects on a fresh session, the abandoned task's eventual outcome is observed (so a
    /// late failure cannot surface as an unobserved task exception), and
    /// <see cref="UnicontaCallTimeoutException"/> is thrown — which the syncs treat as "Uniconta not usable
    /// this tick": warning, skip, retry on the next tick.
    /// </summary>
    /// <param name="timeout">Overrides the configured <c>UnicontaConfig.TimeoutSeconds</c>. Tests only.</param>
    public async Task<T> RunWithTimeoutAsync<T>(Task<T> sdkCall, string operation, TimeSpan? timeout = null)
    {
        // Single chokepoint for every Uniconta call, so this is where the daily budget gets measured.
        UnicontaCallCounter.Increment();

        timeout ??= TimeSpan.FromSeconds(Math.Max(5, _config.TimeoutSeconds));
        if (await Task.WhenAny(sdkCall, Task.Delay(timeout.Value)) == sdkCall)
            return await sdkCall;

        // Never let the abandoned call's exception go unobserved.
        _ = sdkCall.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        _logger.LogWarning(
            "Uniconta call '{Operation}' exceeded {TimeoutSeconds}s; abandoning the wait and forcing a reconnect on the next call",
            operation, timeout.Value.TotalSeconds);
        InvalidateConnection();
        throw new UnicontaCallTimeoutException(operation, timeout.Value);
    }

    /// <summary>
    /// Marks the current session unusable without waiting on the connect gate — a timed-out call has left
    /// the session in an unknown state, so the next <see cref="EnsureConnectedAsync"/> must rebuild it.
    /// </summary>
    private void InvalidateConnection()
    {
        _isLoggedIn = false;
        _session = null;
        _company = null;
    }

    public async Task<CrudAPI> CreateCrudApiAsync()
    {
        await EnsureConnectedAsync();
        return new CrudAPI(_session!, _company!);
    }

    public async Task<QueryAPI> CreateQueryApiAsync()
    {
        await EnsureConnectedAsync();
        return new QueryAPI(_session!, _company!);
    }

    public async Task ReconnectAsync()
    {
        var staleStamp = _connectedAtUtc;
        await _connectGate.WaitAsync();
        try
        {
            if (IsHealthy && _connectedAtUtc != staleStamp)
            {
                // Another caller already reconnected while we were waiting on the gate.
                return;
            }

            _logger.LogInformation("Reconnecting to Uniconta...");
            ResetConnection();
            await ConnectAsync();
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private void ResetConnection()
    {
        _isLoggedIn = false;
        try
        {
            _session?.LogOut();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error logging out previous Uniconta session");
        }
        _session = null;
        _company = null;
    }

    // Caller must hold _connectGate.
    private async Task ConnectAsync()
    {
        if (IsHealthy)
            return;

        try
        {
            // Host + process identity on every handshake. Login/OpenCompany is the single biggest line
            // item in the Uniconta call budget (~54% of runs paid one on 2026-07-28), and the rate is
            // several times what one session with a 150 s max age can produce. Telemetry could not settle
            // whether that is many instances or many worker processes per instance — AppRoleInstance showed
            // three concurrent ids sharing one ordinal, and capping maximumInstanceCount to 1 plus a
            // restart changed nothing. Same host + different process id ⇒ worker processes; different
            // hosts ⇒ instances. Each process holds its own singleton manager, hence its own session.
            _logger.LogInformation(
                "Establishing Uniconta connection... (host {HostName}, process {ProcessId}, managerId {ManagerId})",
                Environment.MachineName, Environment.ProcessId, ProcessInstanceId);
            _connection = new UnicontaConnection(APITarget.Live);
            _session = new Session(_connection);

            _logger.LogInformation("Attempting login for user: {Username}", _config.Username);
            if (!Guid.TryParse(_config.ApiKey, out var apiKeyGuid))
                throw new InvalidOperationException("Uniconta ApiKey is not a valid GUID");

            var loginResult = await RunWithTimeoutAsync(_session.LoginAsync(
                _config.Username,
                _config.Password,
                LoginType.API,
                apiKeyGuid,
                default(Language),
                null), "LoginAsync");
            _isLoggedIn = loginResult == ErrorCodes.Succes;
            if (!_isLoggedIn)
                throw new InvalidOperationException($"Failed to login to Uniconta API: {loginResult}");

            _logger.LogInformation("Successfully logged in to Uniconta API");

            _logger.LogInformation("Opening company with ID: {CompanyId}", _config.CompanyId);
            _company = await OpenCompanyWithRetryAsync();

            _connectedAtUtc = DateTime.UtcNow;
            _logger.LogInformation("Successfully connected to company: {CompanyName} (ID: {CompanyId})", _company.Name, _company.CompanyId);
        }
        catch (UnicontaConnectionUnavailableException ex)
        {
            // Uniconta-side transient. Logged as a warning on purpose — see the exception's docs.
            _logger.LogWarning(ex, "Uniconta connection unavailable this tick; the next tick reconnects");
            _isLoggedIn = false;
            _session = null;
            _company = null;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish Uniconta connection");
            _isLoggedIn = false;
            _session = null;
            _company = null;
            throw;
        }
    }

    private const int OpenCompanyRetryDelayMs = 300;

    // OpenCompany intermittently returns null (or throws) for a valid company id — a fast, server-side
    // transient miss that was crashing otherwise-healthy reconnects (e.g. company 129192). Login has
    // already succeeded by the time we get here, so one immediate retry on the same session heals the
    // miss. A genuinely persistent failure still falls through and throws, so real Uniconta downtime is
    // not hidden. One retry only — failures are fast and the sync timers fire often, so a growing backoff
    // would just delay the next clean run. Caller must hold _connectGate (only called from ConnectAsync).
    private async Task<Company> OpenCompanyWithRetryAsync()
    {
        const int maxRetries = 1;
        for (int attempt = 0; ; attempt++)
        {
            Company? company = null;
            Exception? failure = null;
            try
            {
                company = await RunWithTimeoutAsync(_session!.OpenCompany(_config.CompanyId, true), "OpenCompany");
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (company != null)
                return company;

            if (attempt >= maxRetries)
                throw new UnicontaConnectionUnavailableException(
                    $"Failed to get company with ID: {_config.CompanyId}", failure);

            _logger.LogWarning(
                failure,
                "OpenCompany returned no company for ID {CompanyId} on attempt {Attempt}; retrying once after {DelayMs} ms",
                _config.CompanyId, attempt + 1, OpenCompanyRetryDelayMs);
            await Task.Delay(OpenCompanyRetryDelayMs);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        try
        {
            if (_session != null)
            {
                _logger.LogInformation("Disposing Uniconta session");
                _session.LogOut();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing Uniconta session");
        }
        finally
        {
            _session = null;
            _connection = null;
            _company = null;
            _isLoggedIn = false;
            _connectGate.Dispose();
            _disposed = true;
        }
    }
}
