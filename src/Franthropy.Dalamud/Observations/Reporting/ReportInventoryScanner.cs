using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace Franthropy.Dalamud.Observations.Reporting;

/// <summary>
/// Scan-option flags mirrored from the retired InventoryReporter2 plugin so the
/// report payload contract with the MMF.Server inventory pipeline is unchanged.
/// </summary>
public sealed record ReportInventoryOptions
{
    public bool IncludeArmoury { get; init; }
    public bool IncludeCrystals { get; init; } = true;
    public bool IncludeEquipped { get; init; }
    public bool IncludeSaddlebag { get; init; }
    public bool IncludeItemNames { get; init; } = true;

    public static ReportInventoryOptions Defaults { get; } = new();
}

public sealed record ReportItemSlot
{
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public uint Quantity { get; init; }
    public bool IsHq { get; init; }
    public float Condition { get; init; }
}

public sealed record ReportInventoryBag
{
    public string BagName { get; init; } = string.Empty;
    public IReadOnlyList<ReportItemSlot> Items { get; init; } = Array.Empty<ReportItemSlot>();
}

/// <summary>
/// Scans game inventory containers into report bags. Ported from the retired
/// InventoryReporter2 plugin (InventoryScanner.cs); merging semantics for
/// retainer pages are preserved exactly so downstream consumers see the same
/// bag shapes as before the integration.
/// </summary>
public sealed class ReportInventoryScanner
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static readonly InventoryType[] ArmouryContainers =
    [
        InventoryType.ArmoryBody,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryRings,
        InventoryType.ArmoryWrist,
        InventoryType.ArmorySoulCrystal,
    ];

    private static readonly InventoryType[] RetainerContainers =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
        InventoryType.RetainerGil,
        InventoryType.RetainerMarket,
    ];

    public ReportInventoryScanner(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public List<ReportInventoryBag> ScanPlayerInventory(ReportInventoryOptions options)
    {
        var bags = new List<ReportInventoryBag>();

        bags.AddRange(ScanContainers(PlayerBags, options));

        if (options.IncludeEquipped)
            bags.AddRange(ScanContainers([InventoryType.EquippedItems], options));

        if (options.IncludeArmoury)
            bags.AddRange(ScanContainers(ArmouryContainers, options));

        if (options.IncludeCrystals)
            bags.AddRange(ScanContainers([InventoryType.Crystals], options));

        if (options.IncludeSaddlebag)
            bags.AddRange(ScanContainers(
            [
                InventoryType.SaddleBag1,
                InventoryType.SaddleBag2,
                InventoryType.PremiumSaddleBag1,
                InventoryType.PremiumSaddleBag2,
            ], options));

        return bags;
    }

    public List<ReportInventoryBag> ScanCurrentRetainer(ReportInventoryOptions options)
    {
        var bags = ScanContainers(RetainerContainers, options);
        var mergedBags = new List<ReportInventoryBag>();
        var retainerPagesItems = new Dictionary<uint, ReportItemSlot>();

        foreach (var bag in bags)
        {
            if (bag.BagName.StartsWith("RetainerPage", StringComparison.Ordinal))
            {
                foreach (var item in bag.Items)
                {
                    if (retainerPagesItems.TryGetValue(item.ItemId, out var existing))
                    {
                        retainerPagesItems[item.ItemId] = existing with
                        {
                            Quantity = existing.Quantity + item.Quantity,
                            Condition = Math.Max(existing.Condition, item.Condition),
                        };
                    }
                    else
                    {
                        retainerPagesItems[item.ItemId] = item;
                    }
                }
            }
            else
            {
                mergedBags.Add(bag);
            }
        }

        if (retainerPagesItems.Count > 0)
        {
            mergedBags.Insert(0, new ReportInventoryBag
            {
                BagName = "RetainerInventory",
                Items = retainerPagesItems.Values.ToList(),
            });
        }

        return mergedBags;
    }

    public string? ResolveItemName(uint itemId)
    {
        try
        {
            return dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId)?.Name.ToString();
        }
        catch (Exception ex)
        {
            log.Verbose(ex, $"[Franthropy.Reporting] Could not resolve name for item {itemId}");
            return null;
        }
    }

    private unsafe List<ReportInventoryBag> ScanContainers(InventoryType[] types, ReportInventoryOptions options)
    {
        var bags = new List<ReportInventoryBag>();

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            log.Warning("[Franthropy.Reporting] InventoryManager.Instance() returned null");
            return bags;
        }

        foreach (var type in types)
        {
            try
            {
                var container = inventoryManager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;

                var itemGroups = new Dictionary<uint, ReportItemSlot>();

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;

                    var itemId = slot->ItemId;
                    var quantity = (uint)slot->Quantity;
                    var condition = slot->Condition / 30000f;

                    if (itemGroups.TryGetValue(itemId, out var existing))
                    {
                        itemGroups[itemId] = existing with
                        {
                            Quantity = existing.Quantity + quantity,
                            Condition = Math.Max(existing.Condition, condition),
                        };
                    }
                    else
                    {
                        string? itemName = options.IncludeItemNames ? ResolveItemName(itemId) : null;
                        itemGroups[itemId] = new ReportItemSlot
                        {
                            ItemId = itemId,
                            ItemName = itemName,
                            Quantity = quantity,
                            IsHq = false,
                            Condition = condition,
                        };
                    }
                }

                var items = new List<ReportItemSlot>(itemGroups.Values);

                if (items.Count > 0)
                {
                    bags.Add(new ReportInventoryBag
                    {
                        BagName = type.ToString(),
                        Items = items,
                    });
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, $"[Franthropy.Reporting] Error scanning container {type}");
            }
        }

        return bags;
    }
}
