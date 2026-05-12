using NeroTrade.JDIntegration.Models.Settings;

namespace NeroTrade.JDIntegration.Services.ExternalIntegration.Repositories;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;

public sealed class JdRepository : IJdRepository
{
    private readonly HttpClient _httpClient;
    private readonly JdSettings _settings;
    private readonly ILogger<JdRepository> _logger;

    public JdRepository(HttpClient httpClient, JdSettings settings, ILogger<JdRepository> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
            _httpClient.BaseAddress = new Uri(settings.BaseUrl!, UriKind.Absolute);
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds));
        if (!string.IsNullOrWhiteSpace(settings.BearerToken))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.BearerToken);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<JdAddress>> GetAddressesAsync(CancellationToken cancellationToken)
    {
        var results = new List<JdAddress>();
        var page = 1;
        var pageSize = 999;
        while (true)
        {
            var qs = $"pageNumber={page}&pageSize={pageSize}";
            var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/addresses?{qs}"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("JD GET addresses failed: {Status} {Body}", (int)response.StatusCode, body);
                break;
            }
            var body2 = await response.Content.ReadAsStringAsync(cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var pagePayload = await JsonSerializer.DeserializeAsync<JdPagedResponse<JdAddress>>(stream, cancellationToken: cancellationToken)
                              ?? new JdPagedResponse<JdAddress>();
            if (pagePayload.items != null && pagePayload.items.Count > 0)
                results.AddRange(pagePayload.items);

            var p = pagePayload.pagination;
            if (p == null || p.pageCount <= 1 || p.currentPage >= p.pageCount)
                break;
            page++;
        }
        return results;
    }

    public async Task<(bool ok, int status, string message, JdAddress? returned)> CreateAddressAsync(JdAddress address, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(address), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "api/addresses") { Content = content };
        var response = await SendWithRetryAsync(() => request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (false, (int)response.StatusCode, body, null);
        var ret = JsonSerializer.Deserialize<JdAddress>(body);
        return (true, (int)response.StatusCode, body, ret);
    }

    public async Task<(bool ok, int status, string message)> UpdateAddressAsync(long id, JdAddress address, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(address), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Put, $"api/addresses/{id}") { Content = content };
        var response = await SendWithRetryAsync(() => request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (false, (int)response.StatusCode, body);
        return (true, (int)response.StatusCode, body);
    }

    public async Task<IReadOnlyList<JdCatalogItem>> GetCatalogItemsAsync(CancellationToken cancellationToken)
    {
        var results = new List<JdCatalogItem>();
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/catalog"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("JD GET catalog items failed: {Status} {Body}", (int)response.StatusCode, body);
            return Enumerable.Empty<JdCatalogItem>().ToList();
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var pagePayload = await JsonSerializer.DeserializeAsync<IEnumerable<JdCatalogItem>>(stream, cancellationToken: cancellationToken)
                          ?? [];
        results.AddRange(pagePayload);
        return results;
    }

    public async Task<(bool ok, int status, string message, JdCatalogItem? returned)> CreateCatalogItemAsync(JdCatalogItem item, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(item), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "api/catalog") { Content = content };
        var response = await SendWithRetryAsync(() => request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (false, (int)response.StatusCode, body, null);
        var ret = JsonSerializer.Deserialize<JdCatalogItem>(body);
        return (true, (int)response.StatusCode, body, ret);
    }

    public async Task<(bool ok, int status, string message)> UpdateCatalogItemAsync(long id, JdCatalogItem item, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(item), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Put, $"api/catalog/{id}") { Content = content };
        var response = await SendWithRetryAsync(() => request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (false, (int)response.StatusCode, body);
        return (true, (int)response.StatusCode, body);
    }

    public async Task<IReadOnlyList<JdIncomingShipment>> GetIncomingShipmentsAsync(CancellationToken cancellationToken, int? status = 1)
    {
        var results = new List<JdIncomingShipment>();
        var page = 1;
        var pageSize = 200;
        while (true)
        {
            var qs = $"pageNumber={page}&pageSize={pageSize}";
            if (status.HasValue)
                qs += $"&status={status.Value}";
            var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/incomingshipments?{qs}"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("JD GET incoming shipments failed: {Status} {Body}", (int)response.StatusCode, body);
                break;
            }
            var body2 = await response.Content.ReadAsStringAsync(cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var pagePayload = await JsonSerializer.DeserializeAsync<JdPagedResponse<JdIncomingShipment>>(stream, cancellationToken: cancellationToken)
                              ?? new JdPagedResponse<JdIncomingShipment>();
            if (pagePayload.items != null && pagePayload.items.Count > 0)
                results.AddRange(pagePayload.items);

            var p = pagePayload.pagination;
            if (p == null || p.pageCount <= 1 || p.currentPage >= p.pageCount)
                break;
            page++;
        }
        
        return results;
    }

    public async Task<(bool ok, int status, string message, JdIncomingShipment? returned)> GetIncomingShipmentByIdAsync(long id, CancellationToken cancellationToken)
    {
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/incomingshipments/{id}"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("JD GET incoming shipment by ID failed: {Status} {Body}", (int)response.StatusCode, body);
            return (false, (int)response.StatusCode, body, null);
        }
        var ret = JsonSerializer.Deserialize<JdIncomingShipment>(body);
        return (true, (int)response.StatusCode, body, ret);
    }

    public async Task<(bool ok, int status, string message, JdIncomingShipment? returned)> UpsertIncomingShipmentAsync(JdIncomingShipmentCreate payload, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "api/incomingshipments") { Content = content };
        var response = await SendWithRetryAsync(() => request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("JD POST incoming shipment failed: {Status} {Body}", (int)response.StatusCode, body);
            return (false, (int)response.StatusCode, body, null);
        }
        var ret = JsonSerializer.Deserialize<JdIncomingShipment>(body);
        return (true, (int)response.StatusCode, body, ret);
    }

    public async Task<IReadOnlyList<JdContainerType>> GetContainerTypesAsync(CancellationToken cancellationToken)
    {
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/containertypes"), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return Array.Empty<JdContainerType>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var data = await JsonSerializer.DeserializeAsync<List<JdContainerType>>(stream, cancellationToken: cancellationToken) ?? new List<JdContainerType>();
        return data;
    }

    public async Task<IReadOnlyList<JdInventory>> GetInventoriesAsync(CancellationToken cancellationToken)
    {
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/inventories"), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return Array.Empty<JdInventory>();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var data = await JsonSerializer.DeserializeAsync<List<JdInventory>>(stream, cancellationToken: cancellationToken) ?? new List<JdInventory>();
        return data;
    }

    public async Task<IReadOnlyList<JdRequestOrder>> GetRequestOrdersAsync(long inventoryId, CancellationToken cancellationToken)
    {
        var results = new List<JdRequestOrder>();
        var page = 1;
        var pageSize = 200;
        while (true)
        {
            var qs = $"pageNumber={page}&pageSize={pageSize}";
            var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/inventories/{inventoryId}/requestorders?{qs}"), cancellationToken);
            if (!response.IsSuccessStatusCode)
                break;
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var pagePayload = await JsonSerializer.DeserializeAsync<JdPagedResponse<JdRequestOrder>>(stream, cancellationToken: cancellationToken)
                              ?? new JdPagedResponse<JdRequestOrder>();
            if (pagePayload.items != null && pagePayload.items.Count > 0)
                results.AddRange(pagePayload.items);
            var p = pagePayload.pagination;
            if (p == null || p.pageCount <= 1 || p.currentPage >= p.pageCount)
                break;
            page++;
        }
        return results;
    }

    public async Task<(bool ok, int status, string message, JdRequestOrder? returned)> CreateRequestOrderAsync(long inventoryId, JdRequestOrderCreate payload, CancellationToken cancellationToken)
    {
        //payload.productItems.ForEach(i => i.catalog.sku = "TESTSKU");
        
        // Check if date is in the past and adjust if necessary
        if(payload.date < DateTime.UtcNow)
            payload.date = DateTime.UtcNow;

        var jsonPayload = JsonSerializer.Serialize(payload);
        
        using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/inventories/{inventoryId}/requestorders") { Content = content };
        var response = await SendWithRetryAsync(() => request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (false, (int)response.StatusCode, body, null);
        var ret = JsonSerializer.Deserialize<JdRequestOrder>(body);
        return (true, (int)response.StatusCode, body, ret);
    }

    public async Task<(bool ok, int status, string message)> DeleteRequestOrderAsync(long inventoryId, long requestOrderId, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"api/inventories/{inventoryId}/requestorders/{requestOrderId}");
        var response = await SendWithRetryAsync(() => request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("JD DELETE request order failed: {Status} {Body}", (int)response.StatusCode, body);
            return (false, (int)response.StatusCode, body);
        }
        return (true, (int)response.StatusCode, body);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        const int maxAttempts = 3;
        var delay = TimeSpan.FromSeconds(1);
        for (int attempt = 1; ; attempt++)
        {
            using var request = requestFactory();
            try
            {
                var response = await _httpClient.SendAsync(request, ct);
                if (IsTransient((int)response.StatusCode) && attempt < maxAttempts)
                {
                    _logger.LogWarning("JD transient status {Status}. Retrying attempt {Attempt}/{Max}...", (int)response.StatusCode, attempt, maxAttempts);
                    await Task.Delay(delay, ct);
                    delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
                    continue;
                }
                return response;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
            {
                _logger.LogWarning("JD request timeout. Retrying attempt {Attempt}/{Max}...", attempt, maxAttempts);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
                continue;
            }
        }
    }

    private static bool IsTransient(int statusCode)
        => statusCode == 408 || statusCode == 429 || (statusCode >= 500 && statusCode < 600);

    // File operations
    public async Task<(bool ok, int status, string message, JdFileResponse? returned)> CreateFileAsync(JdFileCreate file, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(file);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/files") { Content = content }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("JD POST files failed: {Status} {Body}", (int)response.StatusCode, body);
            return (false, (int)response.StatusCode, $"HTTP {(int)response.StatusCode}: {body}", null);
        }

        // Log the raw response for debugging
        _logger.LogDebug("JD POST files response: {Body}", body);
        
        var fileResponse = JsonSerializer.Deserialize<JdFileResponse>(body);
        
        // Check if we got the pre-signed URL (note: JD API uses "presignedUrl" with lowercase 's')
        if (string.IsNullOrWhiteSpace(fileResponse?.presignedUrl))
        {
            _logger.LogWarning("JD POST files succeeded but presignedUrl is missing. Response: {Body}", body);
            return (false, (int)response.StatusCode, "No pre-signed URL received from JD", fileResponse);
        }

        return (true, (int)response.StatusCode, "File created successfully", fileResponse);
    }

    public async Task<(bool ok, int status, string message)> VerifyFileAsync(long fileId, CancellationToken cancellationToken)
    {
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, $"api/files/{fileId}/verify"), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("JD POST files/{FileId}/verify failed: {Status} {Body}", fileId, (int)response.StatusCode, body);
            return (false, (int)response.StatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        }

        return (true, (int)response.StatusCode, "File verified successfully");
    }
}


