using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Tests.Equipment;

public sealed class EquipmentLoadoutSolverTests
{
    private static readonly CharacterJobSnapshot Paladin = new(
        19,
        "PLD",
        "Paladin",
        50,
        true,
        1,
        "Tank",
        EquipmentStatSemantic.Strength,
        EquipmentDiscipline.Combat);

    [Fact]
    public void BestOwned_ExcludesPurchasableOffers()
    {
        var owned = Offer(100, "Owned sword", EquipmentSlot.MainHand, 45, EquipmentAcquisitionSourceKind.Owned);
        var market = Offer(101, "Market sword", EquipmentSlot.MainHand, 90, EquipmentAcquisitionSourceKind.MarketBoard, 20_000);

        var plan = new EquipmentLoadoutSolver().Plan(Request(
            EquipmentLoadoutStrategy.BestOwned,
            [owned, market]));

        Assert.Equal(100u, plan.Entries.Single(entry => entry.Position == EquipmentLoadoutPosition.MainHand).Recommended?.Definition.ItemId);
        Assert.Equal(0, plan.AcquisitionCount);
    }

    [Fact]
    public void HighestItemLevel_ChoosesAccessibleUpgradeAndCountsCost()
    {
        var current = Offer(100, "Current sword", EquipmentSlot.MainHand, 45, EquipmentAcquisitionSourceKind.Owned);
        var vendor = Offer(101, "Vendor sword", EquipmentSlot.MainHand, 80, EquipmentAcquisitionSourceKind.GilVendor, 10_000);
        var market = Offer(102, "Market sword", EquipmentSlot.MainHand, 90, EquipmentAcquisitionSourceKind.MarketBoard, 20_000);

        var plan = new EquipmentLoadoutSolver().Plan(Request(
            EquipmentLoadoutStrategy.HighestItemLevel,
            [current, vendor, market],
            new Dictionary<EquipmentLoadoutPosition, EquipmentLoadoutOffer>
            {
                [EquipmentLoadoutPosition.MainHand] = current,
            }));

        var mainHand = plan.Entries.Single(entry => entry.Position == EquipmentLoadoutPosition.MainHand);
        Assert.Equal(102u, mainHand.Recommended?.Definition.ItemId);
        Assert.Equal(45, mainHand.ItemLevelDelta);
        Assert.Equal(20_000ul, plan.EstimatedAcquisitionCost);
    }

    [Fact]
    public void MinimizeSpend_ChoosesTheCheapestRealUpgrade()
    {
        var current = Offer(100, "Current sword", EquipmentSlot.MainHand, 45, EquipmentAcquisitionSourceKind.Owned);
        var cheap = Offer(101, "Small vendor upgrade", EquipmentSlot.MainHand, 50, EquipmentAcquisitionSourceKind.GilVendor, 1_000);
        var expensive = Offer(102, "Large market upgrade", EquipmentSlot.MainHand, 90, EquipmentAcquisitionSourceKind.MarketBoard, 20_000);

        var plan = new EquipmentLoadoutSolver().Plan(Request(
            EquipmentLoadoutStrategy.MinimizeSpend,
            [current, cheap, expensive],
            new Dictionary<EquipmentLoadoutPosition, EquipmentLoadoutOffer>
            {
                [EquipmentLoadoutPosition.MainHand] = current,
            }));

        var mainHand = plan.Entries.Single(entry => entry.Position == EquipmentLoadoutPosition.MainHand);
        Assert.Equal(101u, mainHand.Recommended?.Definition.ItemId);
        Assert.Equal(1_000ul, plan.EstimatedAcquisitionCost);
    }

    [Fact]
    public void RingPositions_DoNotAllocateTheSameOwnedInstanceTwice()
    {
        var first = Offer(200, "First ring", EquipmentSlot.Ring, 70, EquipmentAcquisitionSourceKind.Owned, slotIndex: 1);
        var second = Offer(201, "Second ring", EquipmentSlot.Ring, 60, EquipmentAcquisitionSourceKind.Owned, slotIndex: 2);

        var plan = new EquipmentLoadoutSolver().Plan(Request(
            EquipmentLoadoutStrategy.BestOwned,
            [first, second]));

        var rings = plan.Entries
            .Where(entry => entry.Position is EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing)
            .Select(entry => entry.Recommended?.Definition.ItemId)
            .ToArray();
        Assert.Equal([200u, 201u], rings);
    }

    [Fact]
    public void TwoHandedMainHand_MakesOffHandNotRequired()
    {
        var axe = Offer(
            300,
            "Two-handed axe",
            EquipmentSlot.MainHand,
            50,
            EquipmentAcquisitionSourceKind.GilVendor,
            offHandOccupancy: -1);

        var plan = new EquipmentLoadoutSolver().Plan(Request(
            EquipmentLoadoutStrategy.HighestItemLevel,
            [axe]));

        var offHand = plan.Entries.Single(entry => entry.Position == EquipmentLoadoutPosition.OffHand);
        Assert.False(offHand.IsRequired);
        Assert.False(offHand.IsMissing);
        Assert.Contains("two-handed", offHand.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, plan.MissingSlotCount);
    }

    private static EquipmentLoadoutRequest Request(
        EquipmentLoadoutStrategy strategy,
        IReadOnlyList<EquipmentLoadoutOffer> offers,
        IReadOnlyDictionary<EquipmentLoadoutPosition, EquipmentLoadoutOffer>? current = null) =>
        new(Paladin, 50, strategy, offers, current ?? new Dictionary<EquipmentLoadoutPosition, EquipmentLoadoutOffer>());

    private static EquipmentLoadoutOffer Offer(
        uint itemId,
        string name,
        EquipmentSlot slot,
        uint itemLevel,
        EquipmentAcquisitionSourceKind source,
        uint? price = null,
        int slotIndex = 0,
        sbyte offHandOccupancy = 0)
    {
        var definition = new EquipmentItemDefinition(
            itemId,
            name,
            50,
            itemLevel,
            slot,
            new HashSet<uint> { Paladin.ClassJobId },
            1,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            OffHandOccupancy: offHandOccupancy);
        EquipmentInstanceSnapshot? instance = source == EquipmentAcquisitionSourceKind.Owned
            ? new(
                new(
                    new(1, "Test", 1),
                    "Armory",
                    slotIndex,
                    itemId,
                    false,
                    1,
                    30_000,
                    0,
                    null,
                    [],
                    null,
                    []),
                DateTimeOffset.UtcNow,
                false)
            : null;
        return new(definition, source, source.ToString(), price, instance);
    }
}
