using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.ExternalIntegration.Repositories;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Behavioral tests for <see cref="JdRepository"/>'s read methods, focused on the
/// "200 OK + empty list" path that bypassed PR #2's cache-poisoning fix and caused the
/// 2026-05-26 2158 dead window. Mocks JD via <see cref="StubHttpMessageHandler"/>.
/// </summary>
public class JdRepositoryTests
{
    [Fact]
    public async Task GetInventoriesAsync_TwoHundredWithEmptyList_ThrowsJdLookupFailed()
    {
        var repo = BuildRepo(StubHttpMessageHandler.Returning(HttpStatusCode.OK, "[]"));

        var ex = await Assert.ThrowsAsync<JdLookupFailedException>(
            () => repo.GetInventoriesAsync(CancellationToken.None));
        Assert.Equal("inventories", ex.Endpoint);
        Assert.Equal(200, ex.StatusCode);
    }

    [Fact]
    public async Task GetInventoriesAsync_TwoHundredWithNonEmptyList_ReturnsParsed()
    {
        var body = """[{"id":42,"name":"Main"}]""";
        var repo = BuildRepo(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body));

        var result = await repo.GetInventoriesAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(42, result[0].id);
    }

    [Fact]
    public async Task GetInventoriesAsync_FiveHundred_ThrowsAfterRetries()
    {
        // SendWithRetryAsync retries 3x on 5xx; after exhaustion, the final response surfaces and
        // GetInventoriesAsync throws. The retry count itself is verified via call-count.
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.InternalServerError, "boom");
        var repo = BuildRepo(handler);

        var ex = await Assert.ThrowsAsync<JdLookupFailedException>(
            () => repo.GetInventoriesAsync(CancellationToken.None));
        Assert.Equal(500, ex.StatusCode);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task GetInventoriesAsync_FourHundredOne_ThrowsImmediatelyNoRetry()
    {
        // 401 is non-transient → no retry, throw on first response.
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}");
        var repo = BuildRepo(handler);

        var ex = await Assert.ThrowsAsync<JdLookupFailedException>(
            () => repo.GetInventoriesAsync(CancellationToken.None));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetContainerTypesAsync_TwoHundredWithEmptyList_ThrowsJdLookupFailed()
    {
        var repo = BuildRepo(StubHttpMessageHandler.Returning(HttpStatusCode.OK, "[]"));

        var ex = await Assert.ThrowsAsync<JdLookupFailedException>(
            () => repo.GetContainerTypesAsync(CancellationToken.None));
        Assert.Equal("containertypes", ex.Endpoint);
        Assert.Equal(200, ex.StatusCode);
    }

    [Fact]
    public async Task GetContainerTypesAsync_TwoHundredWithNonEmptyList_ReturnsParsed()
    {
        var body = """[{"id":1,"name":"Stk"}]""";
        var repo = BuildRepo(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body));

        var result = await repo.GetContainerTypesAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Stk", result[0].name);
    }

    private static JdRepository BuildRepo(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub.invalid/") };
        var settings = new JdSettings { BaseUrl = "http://stub.invalid/", TimeoutSeconds = 5 };
        return new JdRepository(http, settings, NullLogger<JdRepository>.Instance);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public int CallCount { get; private set; }

        private StubHttpMessageHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public static StubHttpMessageHandler Returning(HttpStatusCode status, string body)
            => new(status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
