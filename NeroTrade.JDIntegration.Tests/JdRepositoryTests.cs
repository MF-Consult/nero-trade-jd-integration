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

    [Fact]
    public async Task DryRun_PostIsNotSentToJd_AndReportsSuccess()
    {
        // A mutating call must never reach JD in DryRun — the handler should see zero calls — yet the
        // caller still gets a success so the sync proceeds (and logs the would-be payload).
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, "{}");
        var repo = BuildRepo(handler, dryRun: true);

        var result = await repo.UpsertIncomingShipmentAsync(
            new NeroTrade.JDIntegration.Models.ExternalIntegration.JdIncomingShipmentCreate(), CancellationToken.None);

        Assert.True(result.ok);
        Assert.Equal(0, handler.CallCount); // nothing sent to JD
    }

    [Fact]
    public async Task DryRun_GetStillReachesJd()
    {
        // Reads must still execute in DryRun so catalog/container-type/dedup lookups keep working.
        var body = """[{"id":1,"name":"Stk"}]""";
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, body);
        var repo = BuildRepo(handler, dryRun: true);

        var result = await repo.GetContainerTypesAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(1, handler.CallCount); // GET went through
    }

    [Fact]
    public async Task GetRequestOrdersAsync_LaterPageFails_ThrowsInsteadOfReturningTruncatedList()
    {
        // Page 1 says there are 2 pages; page 2 fails. A partial list would make the sales-order dedup
        // believe an existing order is absent in JD and create a duplicate.
        var handler = StubHttpMessageHandler.Sequence(
            (HttpStatusCode.OK, """{"items":[{"id":1}],"pagination":{"currentPage":1,"pageCount":2,"pageSize":200}}"""),
            (HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}"));
        var repo = BuildRepo(handler);

        var ex = await Assert.ThrowsAsync<JdLookupFailedException>(
            () => repo.GetRequestOrdersAsync(16629, CancellationToken.None));
        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("page 2", ex.Endpoint);
    }

    [Fact]
    public async Task GetRequestOrdersAsync_AllPagesOk_ReturnsEveryItem()
    {
        var handler = StubHttpMessageHandler.Sequence(
            (HttpStatusCode.OK, """{"items":[{"id":1}],"pagination":{"currentPage":1,"pageCount":2,"pageSize":200}}"""),
            (HttpStatusCode.OK, """{"items":[{"id":2}],"pagination":{"currentPage":2,"pageCount":2,"pageSize":200}}"""));
        var repo = BuildRepo(handler);

        var result = await repo.GetRequestOrdersAsync(16629, CancellationToken.None);

        Assert.Equal(new long?[] { 1, 2 }, result.Select(o => o.id).ToArray());
    }

    private static JdRepository BuildRepo(HttpMessageHandler handler, bool dryRun = false)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub.invalid/") };
        var settings = new JdSettings { BaseUrl = "http://stub.invalid/", TimeoutSeconds = 5, DryRun = dryRun };
        return new JdRepository(http, settings, NullLogger<JdRepository>.Instance);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private Queue<(HttpStatusCode, string)>? _sequence;

        public int CallCount { get; private set; }

        private StubHttpMessageHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public static StubHttpMessageHandler Returning(HttpStatusCode status, string body)
            => new(status, body);

        /// <summary>Answers successive calls from the list; the last entry repeats once it runs out.</summary>
        public static StubHttpMessageHandler Sequence(params (HttpStatusCode, string)[] responses)
            => new(responses[^1].Item1, responses[^1].Item2) { _sequence = new Queue<(HttpStatusCode, string)>(responses) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var (status, body) = _sequence is { Count: > 1 } ? _sequence.Dequeue() : (_status, _body);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
