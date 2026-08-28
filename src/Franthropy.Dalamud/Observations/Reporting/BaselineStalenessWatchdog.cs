using System;
using System.Threading;
using System.Threading.Tasks;
using Franthropy.Observations.Storage;

namespace Franthropy.Dalamud.Observations.Reporting;

public sealed record BaselineStalenessWatchdogOptions
{
    /// <summary>How often the periodic check runs while logged in.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum age of a complete player-inventory baseline before it is
    /// considered stale even if its projection row says otherwise.</summary>
    public TimeSpan MaxBaselineAge { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Delay after login/startup before the first check.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(1);

    public Func<ulong> CurrentContentId { get; init; } = static () => 0;
    public Func<uint> CurrentHomeWorldId { get; init; } = static () => 0;
}

/// <summary>
/// Watches the shared observation store for missing or aged-out inventory
/// baselines and reports them BY NAME. Added after the 2026-08-27 RMB
/// verification incident, where a stale retainer-market-listings projection
/// silently blocked purchase verification for hours while every layer
/// downstream of the store degraded gracefully into a generic timeout.
/// </summary>
public sealed class BaselineStalenessWatchdog : IDisposable
{
    // ObservationContainerKind values (mirrored to avoid a coupling here).
    private const int ContainerPlayerInventory = 0;
    private const int ContainerRetainerRoster = 1;
    private const int ContainerRetainerInventory = 2;
    private const int ContainerRetainerMarketListings = 3;
    private const int ContainerSaddlebag = 4;
    private const int ContainerRetainerGil = 5;

    private readonly ObservationStoreOptions storeOptions;
    private readonly BaselineStalenessWatchdogOptions options;
    private readonly Action<string> report;
    private readonly CancellationTokenSource lifetime = new();
    private Task? loop;

    public BaselineStalenessWatchdog(
        ObservationStoreOptions storeOptions,
        BaselineStalenessWatchdogOptions options,
        Action<string> report)
    {
        this.storeOptions = storeOptions;
        this.options = options;
        this.report = report;
    }

    public void Start()
    {
        if (loop is not null)
            return;
        loop = Task.Run(() => RunAsync(lifetime.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(options.InitialDelay, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                await CheckOnceAsync(token).ConfigureAwait(false);
                await Task.Delay(options.Interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            report($"Baseline staleness watchdog stopped unexpectedly: {ex.Message}");
        }
    }

    internal async Task CheckOnceAsync(CancellationToken token)
    {
        var contentId = options.CurrentContentId();
        var homeWorldId = options.CurrentHomeWorldId();
        if (contentId == 0 || homeWorldId == 0)
            return; // not logged in / identity not stable yet

        await using var connection = CreateReadOnlyConnection();
        await connection.OpenAsync(token).ConfigureAwait(false);

        var ownerPrefix = $"{contentId:X16}:{homeWorldId}:";

        // 1. Player inventory baseline: must exist, not be stale, and be recent.
        var player = await ReadScopeAsync(connection, $"{ownerPrefix}{contentId:X16}:{ContainerPlayerInventory}", token).ConfigureAwait(false);
        if (player is null)
        {
            report(
                "Inventory baseline is MISSING for the current character " +
                $"(scope {ownerPrefix}...:{ContainerPlayerInventory}); remote purchases will fail price " +
                "verification until a complete inventory capture succeeds.");
        }
        else if (player.IsStale)
        {
            report(
                $"Inventory baseline is STALE for the current character (reason: {player.StaleReason ?? "unknown"}); " +
                "remote purchases will fail price verification until a complete inventory capture succeeds.");
        }
        else if (player.LastConfirmedAtUtc is { } confirmed &&
                 DateTimeOffset.UtcNow - confirmed > options.MaxBaselineAge)
        {
            report(
                $"Inventory baseline is OLD (last confirmed {confirmed:u}, age threshold {options.MaxBaselineAge}); " +
                "if remote purchases fail price verification, this is why.");
        }

        // 2. Stale retainer scopes: these never self-heal until the retainer's
        //    addon refreshes; name them so the remediation is obvious.
        var staleScopes = await ReadStaleScopesAsync(connection, ownerPrefix, token).ConfigureAwait(false);
        foreach (var scope in staleScopes)
        {
            var containerName = DescribeContainer(scope.Container);
            report(
                $"Stale {containerName} projection for retainer {scope.SubjectId:X16} " +
                $"(reason: {scope.StaleReason ?? "unknown"}). This scope rejects all inventory deltas until the " +
                "retainer's corresponding window is opened again so it can be re-captured.");
        }
    }

    private static string DescribeContainer(int container) => container switch
    {
        ContainerPlayerInventory => "player inventory",
        ContainerRetainerRoster => "retainer roster",
        ContainerRetainerInventory => "retainer inventory",
        ContainerRetainerMarketListings => "retainer market listings",
        ContainerSaddlebag => "saddlebag",
        ContainerRetainerGil => "retainer gil",
        _ => $"container {container}",
    };

    private Microsoft.Data.Sqlite.SqliteConnection CreateReadOnlyConnection()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = storeOptions.DatabasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            }.ToString());
        return connection;
    }

    private sealed record ScopeRow(
        string ScopeKey,
        bool IsStale,
        string? StaleReason,
        DateTimeOffset? LastConfirmedAtUtc)
    {
        public int Container => int.Parse(ScopeKey.Split(':')[^1]);
        public ulong SubjectId => Convert.ToUInt64(ScopeKey.Split(':')[^2], 16);
    }

    private static async Task<ScopeRow?> ReadScopeAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string scopeKey,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT scope_key, is_stale, stale_reason, last_confirmed_at_utc
            FROM current_projection WHERE scope_key = $scope_key;
            """;
        command.Parameters.AddWithValue("$scope_key", scopeKey);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            return null;
        return new ScopeRow(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)));
    }

    private static async Task<System.Collections.Generic.List<ScopeRow>> ReadStaleScopesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string ownerPrefix,
        CancellationToken token)
    {
        var results = new System.Collections.Generic.List<ScopeRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT scope_key, is_stale, stale_reason, last_confirmed_at_utc
            FROM current_projection
            WHERE scope_key LIKE $prefix || '%' AND is_stale = 1;
            """;
        command.Parameters.AddWithValue("$prefix", ownerPrefix);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            results.Add(new ScopeRow(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3))));
        }
        return results;
    }

    public void Dispose()
    {
        lifetime.Cancel();
        try
        {
            loop?.GetAwaiter().GetResult();
        }
        catch
        {
            // loop faults are already reported through `report`.
        }
        lifetime.Dispose();
    }
}
