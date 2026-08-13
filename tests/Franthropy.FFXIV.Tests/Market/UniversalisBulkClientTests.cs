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
    public async Task Queued_chunk_receives_its_own_attempt_timeout_after_acquiring_a_request_slot()
    {
        var handler = new FirstRequestTimesOutHandler();
        var client = CreateClient(
            handler,
            chunkSize: 1,
            maxConcurrency: 1,
            maxAttempts: 1,
            attemptTimeout: TimeSpan.FromMilliseconds(50));

        var result = await client.FetchAsync<ItemResponse>(new UniversalisBulkRequest
        {
            WorldOrDataCenter = "Aether",
            ItemIds = [1, 2],
        });

        Assert.Equal([1u, 2u, 1u], handler.RequestedItemIds);
        Assert.Empty(result.MissingItemIds);
        Assert.True(result.Items.ContainsKey(2));
    }

    [Fact]
    public async Task Serial_mode_keeps_split_recovery_requests_serial()
    {
        var handler = new RecordingHandler(
            request => request.RequestUri!.AbsolutePath.EndsWith("/1,2", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
                : BulkResponse(request),
            responseDelay: TimeSpan.FromMilliseconds(30));
        var client = CreateClient(handler, chunkSize: 2, maxConcurrency: 2, maxAttempts: 1);

        var result = await client.FetchAsync<ItemResponse>(new UniversalisBulkRequest
        {
            WorldOrDataCenter = "Aether",
            ItemIds = [1, 2],
            UseParallelRequests = false,
        });

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(1, handler.MaximumConcurrency);
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

    [Fact]
    public async Task MissingItemRecovery_CanBeDisabledForCatalogDiscovery()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{"itemIDs":[1,2],"items":{"1":{"itemID":1}}}"""));
        var client = CreateClient(handler, retryMissingItems: false);

        var result = await client.FetchAsync<ItemResponse>(new UniversalisBulkRequest
        {
            WorldOrDataCenter = "Aether",
            ItemIds = [1, 2],
        });

        Assert.Single(result.Items);
        Assert.Equal([2u], result.MissingItemIds);
        Assert.Single(handler.Requests);
    }

    private static UniversalisBulkClient CreateClient(
        HttpMessageHandler handler,
        int chunkSize = 10,
        int maxConcurrency = 2,
        int maxAttempts = 2,
        TimeSpan? attemptTimeout = null,
        bool retryMissingItems = true) =>
        new(
            new HttpClient(handler),
            new Uri("https://example.test/api/v2/"),
            new UniversalisBulkClientOptions
            {
                ChunkSize = chunkSize,
                MaxConcurrentRequests = maxConcurrency,
                MaxAttemptsPerChunk = maxAttempts,
                RetryMissingItems = retryMissingItems,
                MinimumRequestSpacing = TimeSpan.Zero,
                DefaultRetryDelay = TimeSpan.Zero,
                DefaultRateLimitCooldown = TimeSpan.Zero,
                AttemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(2),
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

    private sealed class FirstRequestTimesOutHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<uint> requestedItemIds = new();
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);
        public IReadOnlyList<uint> RequestedItemIds => requestedItemIds.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            requestedItemIds.Enqueue(uint.Parse(request.RequestUri!.AbsolutePath.Split('/').Last()));
            if (Interlocked.Increment(ref requestCount) == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return BulkResponse(request);
        }
    }

    private sealed record ItemResponse
    {
        [JsonPropertyName("itemID")]
        public uint ItemId { get; init; }
    }
}
