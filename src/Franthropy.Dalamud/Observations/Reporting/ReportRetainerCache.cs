using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Dalamud.Plugin.Services;

namespace Franthropy.Dalamud.Observations.Reporting;

public sealed record CachedRetainerReport
{
    public ulong RetainerId { get; init; }
    public string RetainerName { get; init; } = string.Empty;
    public DateTime LastUpdated { get; init; }
    public List<ReportInventoryBag> Bags { get; init; } = new();
}

/// <summary>
/// Snapshot cache of retainer inventories, taken when a retainer inventory
/// window closes. Ported from the retired InventoryReporter2 plugin
/// (RetainerCacheManager.cs); persistence moved from Dalamud plugin config to
/// a JSON file under the Franthropy config directory because this code now
/// lives inside a hosting plugin rather than its own Dalamud plugin.
/// </summary>
public sealed class ReportRetainerCache : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;
    private readonly ReportInventoryScanner scanner;
    private readonly ReportInventoryOptions options;
    private readonly string cacheFilePath;
    private readonly object gate = new();
    private bool isBatchRefreshActive;

    // Both addon names are registered so the handler fires regardless of
    // which layout the game uses (depends on player's bag count / resolution).
    private const string LargeAddon = "InventoryRetainerLarge";
    private const string SmallAddon = "InventoryRetainer";

    private ulong activeRetainerId;
    private string activeRetainerName = string.Empty;

    /// <summary>Raised after a retainer has been successfully cached.</summary>
    public event Action? RetainerCached;

    public ReportRetainerCache(
        IAddonLifecycle addonLifecycle,
        IPluginLog log,
        ReportInventoryScanner scanner,
        string franthropyConfigDirectory,
        ReportInventoryOptions? options = null)
    {
        this.addonLifecycle = addonLifecycle;
        this.log = log;
        this.scanner = scanner;
        this.options = options ?? ReportInventoryOptions.Defaults;
        cacheFilePath = Path.Combine(franthropyConfigDirectory, "report-retainer-cache.json");

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, LargeAddon, OnRetainerWindowOpen);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, LargeAddon, OnRetainerWindowClose);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, SmallAddon, OnRetainerWindowOpen);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, SmallAddon, OnRetainerWindowClose);
    }

    public IReadOnlyList<CachedRetainerReport> Snapshot()
    {
        lock (gate)
        {
            return Load().Retainers.ToList();
        }
    }

    private CacheFile Load()
    {
        try
        {
            if (!File.Exists(cacheFilePath))
                return new CacheFile();
            return JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cacheFilePath)) ?? new CacheFile();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Franthropy.Reporting] Could not read the retainer report cache; starting empty.");
            return new CacheFile();
        }
    }

    private void Save(CacheFile file)
    {
        var directory = Path.GetDirectoryName(cacheFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(cacheFilePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
    }

    private unsafe void OnRetainerWindowOpen(AddonEvent type, AddonArgs args)
    {
        try
        {
            var rm = RetainerManager.Instance();
            if (rm == null)
                return;

            var activeRetainer = rm->GetActiveRetainer();
            if (activeRetainer == null || activeRetainer->RetainerId == 0)
            {
                log.Warning("[Franthropy.Reporting] Retainer window opened but no active retainer was found.");
                return;
            }

            activeRetainerId = activeRetainer->RetainerId;
            fixed (byte* namePtr = activeRetainer->Name)
            {
                activeRetainerName = Marshal.PtrToStringUTF8((nint)namePtr, 32)
                                     ?.Split('\0')[0]
                                     ?? string.Empty;
            }

            log.Debug($"[Franthropy.Reporting] Retainer window opened for '{activeRetainerName}' (id={activeRetainerId})");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Franthropy.Reporting] Error in OnRetainerWindowOpen");
        }
    }

    private unsafe void OnRetainerWindowClose(AddonEvent type, AddonArgs args)
    {
        if (activeRetainerId == 0)
        {
            log.Warning("[Franthropy.Reporting] Retainer window closed but active retainer ID is unknown — skipping cache.");
            return;
        }

        try
        {
            var bags = scanner.ScanCurrentRetainer(options);
            lock (gate)
            {
                var file = Load();
                var retainers = file.Retainers.Where(r => r.RetainerId != activeRetainerId).ToList();
                retainers.Add(new CachedRetainerReport
                {
                    RetainerId = activeRetainerId,
                    RetainerName = activeRetainerName,
                    LastUpdated = DateTime.UtcNow,
                    Bags = bags.ToList(),
                });
                Save(new CacheFile { Retainers = retainers });
            }

            RetainerCached?.Invoke();

            // Batch-refresh suppression is owned by the caller via
            // BeginBatchRefresh/EndBatchRefresh; the report send itself is
            // triggered by the hosting plugin.
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Franthropy.Reporting] Error caching retainer inventory");
        }
        finally
        {
            activeRetainerId = 0;
            activeRetainerName = string.Empty;
        }
    }

    public void BeginBatchRefresh() => isBatchRefreshActive = true;

    public void EndBatchRefresh() => isBatchRefreshActive = false;

    public bool IsBatchRefreshActive
    {
        get { lock (gate) return isBatchRefreshActive; }
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, LargeAddon, OnRetainerWindowOpen);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, LargeAddon, OnRetainerWindowClose);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, SmallAddon, OnRetainerWindowOpen);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, SmallAddon, OnRetainerWindowClose);
    }
}

internal sealed class CacheFile
{
    [JsonPropertyName("retainers")]
    public List<CachedRetainerReport> Retainers { get; init; } = new();
}
