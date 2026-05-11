using NeroTrade.JDIntegration.Models.Settings;

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
    private UnicontaConnection? _connection;
    private Session? _session;
    private Company? _company;
    private bool _isLoggedIn;
    private bool _disposed;
    private DateTime _connectedAtUtc;

    // The connection manager is a singleton, so a session can live for the lifetime of the worker
    // process. Uniconta sessions time out server-side after a period of inactivity (and can also be
    // dropped by nightly server restarts), so we proactively reconnect once a session reaches this age.
    private static readonly TimeSpan MaxSessionAge = TimeSpan.FromMinutes(30);

    public UnicontaConnectionManager(ILogger<UnicontaConnectionManager> logger, UnicontaConfig config)
    {
        _logger = logger;
        _config = config;
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

    private async Task EnsureConnectedAsync()
    {
        if (_isLoggedIn && _session != null && _company != null)
        {
            if (DateTime.UtcNow - _connectedAtUtc <= MaxSessionAge)
                return;

            _logger.LogInformation("Uniconta session is older than {Age}, reconnecting proactively", MaxSessionAge);
            await ReconnectAsync();
            return;
        }

        await ConnectAsync();
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

    private async Task ConnectAsync()
    {
        try
        {
            if (_isLoggedIn && _session != null && _company != null)
                return;

            _logger.LogInformation("Establishing Uniconta connection...");
            _connection = new UnicontaConnection(APITarget.Live);
            _session = new Session(_connection);

            _logger.LogInformation("Attempting login for user: {Username}", _config.Username);
            if (!Guid.TryParse(_config.ApiKey, out var apiKeyGuid))
                throw new InvalidOperationException("Uniconta ApiKey is not a valid GUID");

            var loginResult = await _session.LoginAsync(
                _config.Username,
                _config.Password,
                LoginType.API,
                apiKeyGuid,
                default(Language),
                null);
            _isLoggedIn = loginResult == ErrorCodes.Succes;
            if (!_isLoggedIn)
                throw new InvalidOperationException($"Failed to login to Uniconta API: {loginResult}");

            _logger.LogInformation("Successfully logged in to Uniconta API");

            _logger.LogInformation("Opening company with ID: {CompanyId}", _config.CompanyId);
            _company = await _session.OpenCompany(_config.CompanyId, true);
            if (_company == null)
                throw new InvalidOperationException($"Failed to get company with ID: {_config.CompanyId}");

            _connectedAtUtc = DateTime.UtcNow;
            _logger.LogInformation("Successfully connected to company: {CompanyName} (ID: {CompanyId})", _company.Name, _company.CompanyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish Uniconta connection");
            _isLoggedIn = false;
            throw;
        }
    }

    public async Task<CrudAPI> CreateCrudApiAsync()
    {
        var session = await GetSessionAsync();
        var company = await GetCompanyAsync();
        return new CrudAPI(session, company);
    }

    public async Task<QueryAPI> CreateQueryApiAsync()
    {
        var session = await GetSessionAsync();
        var company = await GetCompanyAsync();
        return new QueryAPI(session, company);
    }

    public async Task ReconnectAsync()
    {
        _logger.LogInformation("Reconnecting to Uniconta...");
        _isLoggedIn = false;
        _session?.LogOut();
        _session = null;
        _company = null;
        await ConnectAsync();
    }

    public bool IsConnected => _isLoggedIn && _session != null && _company != null;

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
            _disposed = true;
        }
    }
}


