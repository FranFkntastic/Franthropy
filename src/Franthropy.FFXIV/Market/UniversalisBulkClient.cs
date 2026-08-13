using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Franthropy.FFXIV.Market;

public sealed record UniversalisBulkRequest
{
    public required string WorldOrDataCenter { get; init; }

    public required IReadOnlyCollection<uint> ItemIds { get; init; }

    public int? ListingsPerItem { get; init; }

    public int? HistoryEntriesPerItem { get; init; }

    public bool? HqOnly { get; init; }

    public bool UseParallelRequests { get; init; } = true;
}

public sealed record UniversalisBulkFailure
{
    public required IReadOnlyList<uint> ItemIds { get; init; }

    public required string Message { get; init; }

    public HttpStatusCode? StatusCode { get; init; }
}

public sealed record UniversalisBulkResult<TItem>
{
    public required IReadOnlyDictionary<uint, TItem> Items { get; init; }

    public required IReadOnlyList<uint> MissingItemIds { get; init; }

    public required IReadOnlyList<UniversalisBulkFailure> Failures { get; init; }
}

public sealed record UniversalisBulkClientOptions
{
    public int ChunkSize { get; init; } = 10;

    public int MaxConcurrentRequests { get; init; } = UniversalisBulkClient.DefaultMaxConcurrentRequests;

    public int MaxAttemptsPerChunk { get; init; } = 2;

    public int MaximumSplitDepth { get; init; } = 2;

    public bool RetryMissingItems { get; init; } = true;

    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MinimumRequestSpacing { get; init; } = TimeSpan.FromMilliseconds(150);

    public TimeSpan DefaultRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan DefaultRateLimitCooldown { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaximumRateLimitCooldown { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Fetches Universalis item data in bounded, retryable bulk requests while leaving
/// response ownership with the consuming product.
/// </summary>
public sealed class UniversalisBulkClient
{
    public const int DefaultMaxConcurrentRequests = 2;

    private static readonly Uri DefaultBaseUri = new("https://universalis.app/api/v2/");
    private readonly HttpClient httpClient;
    private readonly Uri baseUri;
    private readonly UniversalisBulkClientOptions options;
    private readonly SemaphoreSlim requestGate;
    private readonly SemaphoreSlim requestStartGate = new(1, 1);
    private readonly object requestScheduleSync = new();
    private DateTimeOffset nextRequestAtUtc = DateTimeOffset.MinValue;

    public UniversalisBulkClient(
        HttpClient httpClient,
        Uri? baseUri = null,
        UniversalisBulkClientOptions? options = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.baseUri = baseUri ?? DefaultBaseUri;
        this.options = options ?? new UniversalisBulkClientOptions();

        if (this.options.ChunkSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options), "Chunk size must be between 1 and 100.");
        if (this.options.MaxConcurrentRequests < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "At least one concurrent request is required.");
        if (this.options.MaxAttemptsPerChunk < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "At least one attempt per chunk is required.");
        if (this.options.AttemptTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Attempt timeout must be positive.");

        requestGate = new SemaphoreSlim(
            this.options.MaxConcurrentRequests,
            this.options.MaxConcurrentRequests);
    }

    public async Task<UniversalisBulkResult<TItem>> FetchAsync<TItem>(
        UniversalisBulkRequest request,
        JsonSerializerOptions? serializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.WorldOrDataCenter))
            throw new ArgumentException("A world, data center, or region is required.", nameof(request));

        var itemIds = request.ItemIds
            .Where(itemId => itemId > 0)
            .Distinct()
            .ToArray();
        if (itemIds.Length != request.ItemIds.Count)
            throw new ArgumentException("Item IDs must be non-zero and unique.", nameof(request));
        if (request.ListingsPerItem is < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Listings per item cannot be negative.");
        if (request.HistoryEntriesPerItem is < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "History entries per item cannot be negative.");
        if (itemIds.Length == 0)
        {
            return new UniversalisBulkResult<TItem>
            {
                Items = new Dictionary<uint, TItem>(),
                MissingItemIds = [],
                Failures = [],
            };
        }

        var items = new ConcurrentDictionary<uint, TItem>();
        var failures = new ConcurrentBag<UniversalisBulkFailure>();
        var chunks = itemIds.Chunk(options.ChunkSize).Select(ids => ids.ToArray()).ToArray();

        await ProcessChunksAsync(
            chunks,
            request,
            serializerOptions,
            items,
            failures,
            cancellationToken).ConfigureAwait(false);

        var missing = itemIds.Where(itemId => !items.ContainsKey(itemId)).ToArray();
        if (missing.Length > 0 && options.RetryMissingItems)
        {
            var retryChunks = missing
                .Chunk(Math.Min(options.ChunkSize, 5))
                .Select(ids => ids.ToArray())
                .ToArray();
            await ProcessChunksAsync(
                retryChunks,
                request,
                serializerOptions,
                items,
                failures,
                cancellationToken).ConfigureAwait(false);
            missing = itemIds.Where(itemId => !items.ContainsKey(itemId)).ToArray();
        }

        var missingSet = missing.ToHashSet();
        return new UniversalisBulkResult<TItem>
        {
            Items = new Dictionary<uint, TItem>(items),
            MissingItemIds = missing,
            Failures = failures
                .Where(failure => failure.ItemIds.Any(missingSet.Contains))
                .ToArray(),
        };
    }

    private async Task ProcessChunksAsync<TItem>(
        IReadOnlyList<uint[]> chunks,
        UniversalisBulkRequest request,
        JsonSerializerOptions? serializerOptions,
        ConcurrentDictionary<uint, TItem> items,
        ConcurrentBag<UniversalisBulkFailure> failures,
        CancellationToken cancellationToken)
    {
        if (request.UseParallelRequests)
        {
            await Task.WhenAll(chunks.Select(chunk => FetchChunkWithRecoveryAsync(
                chunk,
                request,
                serializerOptions,
                items,
                failures,
                splitDepth: 0,
                cancellationToken))).ConfigureAwait(false);
            return;
        }

        foreach (var chunk in chunks)
        {
            await FetchChunkWithRecoveryAsync(
                chunk,
                request,
                serializerOptions,
                items,
                failures,
                splitDepth: 0,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FetchChunkWithRecoveryAsync<TItem>(
        uint[] itemIds,
        UniversalisBulkRequest request,
        JsonSerializerOptions? serializerOptions,
        ConcurrentDictionary<uint, TItem> items,
        ConcurrentBag<UniversalisBulkFailure> failures,
        int splitDepth,
        CancellationToken cancellationToken)
    {
        var outcome = await FetchChunkAsync<TItem>(
            itemIds,
            request,
            serializerOptions,
            cancellationToken).ConfigureAwait(false);

        foreach (var item in outcome.Items)
            items[item.Key] = item.Value;

        if (outcome.Failure is null)
            return;

        if (outcome.ShouldSplit &&
            itemIds.Length > 1 &&
            splitDepth < options.MaximumSplitDepth)
        {
            var midpoint = itemIds.Length / 2;
            var halves = new[]
            {
                itemIds[..midpoint],
                itemIds[midpoint..],
            };
            if (request.UseParallelRequests)
            {
                await Task.WhenAll(halves.Select(half => FetchChunkWithRecoveryAsync(
                    half,
                    request,
                    serializerOptions,
                    items,
                    failures,
                    splitDepth + 1,
                    cancellationToken))).ConfigureAwait(false);
            }
            else
            {
                foreach (var half in halves)
                {
                    await FetchChunkWithRecoveryAsync(
                        half,
                        request,
                        serializerOptions,
                        items,
                        failures,
                        splitDepth + 1,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            return;
        }

        failures.Add(outcome.Failure);
    }

    private async Task<ChunkOutcome<TItem>> FetchChunkAsync<TItem>(
        uint[] itemIds,
        UniversalisBulkRequest request,
        JsonSerializerOptions? serializerOptions,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        HttpStatusCode? lastStatusCode = null;
        var shouldSplit = false;

        for (var attempt = 1; attempt <= options.MaxAttemptsPerChunk; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 1)
            {
                var delay = TimeSpan.FromMilliseconds(
                    options.DefaultRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 2));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var uri = BuildRequestUri(request, itemIds);
                using var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);
                lastStatusCode = response.StatusCode;

                if (!response.IsSuccessStatusCode)
                {
                    if (!IsTransient(response.StatusCode))
                    {
                        return ChunkOutcome<TItem>.Failed(
                            itemIds,
                            $"Universalis returned {(int)response.StatusCode} {response.StatusCode}.",
                            response.StatusCode,
                            shouldSplit: false);
                    }

                    shouldSplit = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout;
                    lastException = new HttpRequestException(
                        $"Universalis returned {(int)response.StatusCode} {response.StatusCode}.",
                        null,
                        response.StatusCode);
                    continue;
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var document = await JsonDocument
                    .ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return ParseResponse<TItem>(document.RootElement, itemIds, serializerOptions);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                shouldSplit = true;
                lastException = new TimeoutException(
                    $"Universalis did not return item data within {options.AttemptTimeout.TotalSeconds:N0} seconds.");
            }
            catch (HttpRequestException ex)
            {
                lastStatusCode = ex.StatusCode;
                lastException = ex;
                shouldSplit = ex.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout;
            }
            catch (JsonException ex)
            {
                lastException = ex;
                shouldSplit = false;
            }
        }

        return ChunkOutcome<TItem>.Failed(
            itemIds,
            lastException?.Message ?? "Universalis request failed.",
            lastStatusCode,
            shouldSplit);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(options.AttemptTimeout);
            var response = await httpClient.GetAsync(uri, attemptCancellation.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                ApplyRateLimitCooldown(response);
            return response;
        }
        finally
        {
            requestGate.Release();
        }
    }

    private async Task WaitForRequestSlotAsync(CancellationToken cancellationToken)
    {
        await requestStartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                TimeSpan delay;
                lock (requestScheduleSync)
                    delay = nextRequestAtUtc - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                lock (requestScheduleSync)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (nextRequestAtUtc > now)
                        continue;
                    nextRequestAtUtc = now + options.MinimumRequestSpacing;
                    return;
                }
            }
        }
        finally
        {
            requestStartGate.Release();
        }
    }

    private void ApplyRateLimitCooldown(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (!retryAfter.HasValue && response.Headers.RetryAfter?.Date is { } retryAt)
            retryAfter = retryAt - DateTimeOffset.UtcNow;

        var cooldown = retryAfter.GetValueOrDefault(options.DefaultRateLimitCooldown);
        cooldown = cooldown < options.MinimumRequestSpacing
            ? options.MinimumRequestSpacing
            : cooldown > options.MaximumRateLimitCooldown
                ? options.MaximumRateLimitCooldown
                : cooldown;
        var resumeAt = DateTimeOffset.UtcNow + cooldown;
        lock (requestScheduleSync)
        {
            if (resumeAt > nextRequestAtUtc)
                nextRequestAtUtc = resumeAt;
        }
    }

    private Uri BuildRequestUri(UniversalisBulkRequest request, IReadOnlyList<uint> itemIds)
    {
        var scope = Uri.EscapeDataString(request.WorldOrDataCenter.Trim().Replace(' ', '-'));
        var path = $"{scope}/{string.Join(",", itemIds)}";
        var query = new List<string>();
        if (request.ListingsPerItem.HasValue)
            query.Add($"listings={request.ListingsPerItem.Value}");
        if (request.HistoryEntriesPerItem.HasValue)
            query.Add($"entries={request.HistoryEntriesPerItem.Value}");
        if (request.HqOnly.HasValue)
            query.Add($"hq={request.HqOnly.Value.ToString().ToLowerInvariant()}");
        return new Uri(baseUri, query.Count == 0 ? path : $"{path}?{string.Join("&", query)}");
    }

    private static ChunkOutcome<TItem> ParseResponse<TItem>(
        JsonElement root,
        IReadOnlyList<uint> requestedItemIds,
        JsonSerializerOptions? serializerOptions)
    {
        var items = new Dictionary<uint, TItem>();
        if (requestedItemIds.Count == 1 &&
            (!root.TryGetProperty("items", out var singleItems) || singleItems.ValueKind != JsonValueKind.Object))
        {
            var item = root.Deserialize<TItem>(serializerOptions);
            if (item is not null)
                items[requestedItemIds[0]] = item;
            return ChunkOutcome<TItem>.Succeeded(items);
        }

        if (!root.TryGetProperty("items", out var itemsElement) ||
            itemsElement.ValueKind != JsonValueKind.Object)
        {
            return ChunkOutcome<TItem>.Failed(
                requestedItemIds,
                "Universalis bulk response did not include an items object.",
                null,
                shouldSplit: false);
        }

        foreach (var property in itemsElement.EnumerateObject())
        {
            if (!uint.TryParse(property.Name, out var itemId) ||
                !requestedItemIds.Contains(itemId))
            {
                continue;
            }

            var item = property.Value.Deserialize<TItem>(serializerOptions);
            if (item is not null)
                items[itemId] = item;
        }

        return ChunkOutcome<TItem>.Succeeded(items);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private sealed record ChunkOutcome<TItem>(
        IReadOnlyDictionary<uint, TItem> Items,
        UniversalisBulkFailure? Failure,
        bool ShouldSplit)
    {
        public static ChunkOutcome<TItem> Succeeded(IReadOnlyDictionary<uint, TItem> items) =>
            new(items, null, false);

        public static ChunkOutcome<TItem> Failed(
            IReadOnlyList<uint> itemIds,
            string message,
            HttpStatusCode? statusCode,
            bool shouldSplit) =>
            new(
                new Dictionary<uint, TItem>(),
                new UniversalisBulkFailure
                {
                    ItemIds = itemIds.ToArray(),
                    Message = message,
                    StatusCode = statusCode,
                },
                shouldSplit);
    }
}
