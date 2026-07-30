using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Franthropy.FFXIV.Market;

namespace Franthropy.FFXIV.Tests.Market;

public sealed class UniversalisBulkClientTests
{
    [Fact]
    public async Task NineItems_AreFetchedInOneRequestWithRequestedEvidenceDepth()
    {
        var handler = new RecordingHandler(request => BulkResponse(request));
        var client = CreateClient(handler);

        var result = await client.FetchAsync<ItemResponse>(new UniversalisBulkRequest
        {
            WorldOrDataCenter = "North America",
            ItemIds = Enumerable.Range(1, 9).Select(value => (uint)value).ToArray(),
            ListingsPerItem = 100,
            HistoryEntriesPerItem = 0,
        });

        Assert.Equal(9, result.Items.Count);
        Assert.Empty(result.MissingItemIds);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "/api/v2/North-America/1,2,3,4,5,6,7,8,9?listings=100&entries=0",
            request.PathAndQuery);
    }

    [Fact]
    public async Task TransientGatewayFailure_IsRetriedWithoutDiscardingTheBatch()
    {
        var attempts = 0;
        var handler = new RecordingHandler(request =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            return BulkResponse(request);
        });
        var client = CreateClient(handler);

        var result = await client.FetchAsync<ItemResponse>(new UniversalisBulkRequest
        {
            WorldOrDataCenter = "Aether",
            ItemIds = [5339, 5364],
            ListingsPerItem = 100,
        });

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, result.Items.Count);
        Assert.Empty(result.MissingItemIds);
    }

    [Fact]
    public async Task ConcurrentChunks_NeverExceedTheConfiguredRequestLimit()
    {
        var handler = new RecordingHandler(
            request => BulkResponse(request),
            responseDelay: TimeSpan.FromMilliseconds(30));
        var client = CreateClient(handler, chunkSize: 2, maxConcurrency: 2);

        var result = await client.FetchAsync<ItemResponse>(new UniversalisBulkRequest
        {
            WorldOrDataCenter = "Aether",
            ItemIds = Enumerable.Range(1, 10).Select(value => (uint)value).ToArray(),
        });

        Assert.Equal(10, result.Items.Count);
        Assert.InRange(handler.MaximumConcurrency, 1, 2);
    }

    [Fact]
    public async Task MissingItems_AreRetriedThenReportedExplicitly()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{"itemIDs":[1,2],"items":{"1":{"itemID":1}}}"""));
        var client = CreateClient(handler);

        var result = await client.FetchAsync<ItemResponse>(new UniversalisBulkRequest
        {
            WorldOrDataCenter = "Aether",
            ItemIds = [1, 2],
        });

        Assert.Single(result.Items);
        Assert.Equal([2u], result.MissingItemIds);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static UniversalisBulkClient CreateClient(
        HttpMessageHandler handler,
        int chunkSize = 10,
        int maxConcurrency = 2) =>
        new(
            new HttpClient(handler),
            new Uri("https://example.test/api/v2/"),
            new UniversalisBulkClientOptions
            {
                ChunkSize = chunkSize,
                MaxConcurrentRequests = maxConcurrency,
                MinimumRequestSpacing = TimeSpan.Zero,
                DefaultRetryDelay = TimeSpan.Zero,
                DefaultRateLimitCooldown = TimeSpan.Zero,
                AttemptTimeout = TimeSpan.FromSeconds(2),
            });

    private static HttpResponseMessage BulkResponse(HttpRequestMessage request)
    {
        var itemIds = request.RequestUri!.AbsolutePath
            .Split('/')
            .Last()
            .Split(',')
            .Select(uint.Parse)
            .ToArray();
        var items = string.Join(
            ",",
            itemIds.Select(itemId => $"\"{itemId}\":{{\"itemID\":{itemId}}}"));
        return JsonResponse(
            HttpStatusCode.OK,
            $"{{\"itemIDs\":[{string.Join(",", itemIds)}],\"items\":{{{items}}}}}");
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        TimeSpan? responseDelay = null) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Uri> requests = new();
        private int activeRequests;
        private int maximumConcurrency;

        public IReadOnlyList<Uri> Requests => requests.ToArray();

        public int MaximumConcurrency => Volatile.Read(ref maximumConcurrency);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            requests.Enqueue(request.RequestUri!);
            var active = Interlocked.Increment(ref activeRequests);
            UpdateMaximum(active);
            try
            {
                if (responseDelay > TimeSpan.Zero)
                    await Task.Delay(responseDelay.Value, cancellationToken);
                return responseFactory(request);
            }
            finally
            {
                Interlocked.Decrement(ref activeRequests);
            }
        }

        private void UpdateMaximum(int active)
        {
            var observed = Volatile.Read(ref maximumConcurrency);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(ref maximumConcurrency, active, observed);
                if (previous == observed)
                    return;
                observed = previous;
            }
        }
    }

    private sealed record ItemResponse
    {
        [JsonPropertyName("itemID")]
        public uint ItemId { get; init; }
    }
}
