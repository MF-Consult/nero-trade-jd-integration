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

    public UnicontaConnectionManager(ILogger<UnicontaConnectionManager> logger, UnicontaConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task<Session> GetSessionAsync()
    {
        if (_session != null && _isLoggedIn)
            return _session;

        await ConnectAsync();
        return _session!;
    }

    public async Task<Company> GetCompanyAsync()
    {
        if (_company != null && _isLoggedIn)
            return _company;

        await ConnectAsync();
        return _company!;
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


