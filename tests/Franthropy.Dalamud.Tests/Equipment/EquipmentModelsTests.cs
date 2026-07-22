using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Tests.Equipment;

public sealed class EquipmentModelsTests
{
    [Fact]
    public void Instance_fingerprint_identity_includes_materia_grade()
    {
        var original = Fingerprint([11]);
        var remelded = Fingerprint([12]);

        Assert.False(EquipmentInstanceFingerprintComparer.Instance.Equals(original, remelded));
        Assert.NotEqual(
            EquipmentInstanceFingerprintComparer.Instance.GetHashCode(original),
            EquipmentInstanceFingerprintComparer.Instance.GetHashCode(remelded));
    }

    [Fact]
    public void Gearset_reference_preserves_canonical_ring_side_and_saved_materia()
    {
        var reference = new GearsetItemReference(
            EquipmentSlot.Ring,
            10_001,
            true,
            EquipmentLoadoutPosition.LeftRing,
            [501],
            [12],
            20_001,
            [3, 4]);

        Assert.Equal(EquipmentLoadoutPosition.LeftRing, reference.Position);
        Assert.Equal([501u], reference.MateriaIds);
        Assert.Equal([(byte)12], reference.MateriaGrades);
    }

    private static EquipmentInstanceFingerprint Fingerprint(IReadOnlyList<byte> materiaGrades) =>
        new(
            new CharacterScope(77, "Advisor", 21),
            "ArmoryHead",
            3,
            10_001,
            false,
            1,
            30_000,
            0,
            null,
            [501],
            null,
            [0, 0],
            materiaGrades);
}
