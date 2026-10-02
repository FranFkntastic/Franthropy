using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Reflection;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Franthropy.Dalamud.Diagnostics;

namespace Franthropy.Dalamud.Tests.Diagnostics;

public sealed class NativeCapabilityGuardTests
{
    [Theory]
    [InlineData(0x1000)]
    [InlineData(0x2400)]
    public void RelocatedUniqueCapabilityIsAvailableWithoutBuildApproval(int address)
        => Assert.Equal((nint)address, NativeCapabilityGuard.ResolveUnique([(nint)address], "Retainer command"));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void MissingOrAmbiguousSignaturesCannotProduceCallableAddress(int count)
    {
        var matches = Enumerable.Range(1, count).Select(value => (nint)(value * 0x1000)).ToArray();
        Assert.Throws<NativeCapabilityUnavailableException>(() => NativeCapabilityGuard.ResolveUnique(matches, "Retainer command"));
    }

    [Fact]
    public void MissingDependencyFailsWithItsNameWhileIndependentCapabilityRemainsAvailable()
    {
        var error = Assert.Throws<NativeCapabilityUnavailableException>(() => NativeCapabilityGuard.RequireAddress(0, "UI callback"));
        Assert.Contains("UI callback", error.Message);
        Assert.Equal((nint)0x1000, NativeCapabilityGuard.ResolveUnique([(nint)0x1000], "Independent observer"));
    }

    [Theory]
    [InlineData(10, -1, 1)]
    [InlineData(10, 9, 2)]
    [InlineData(10, 0, 0)]
    public void UnsupportedEnclosingLayoutFailsBeforeNativeUse(int size, int offset, int fieldSize)
        => Assert.Throws<NativeCapabilityUnavailableException>(() => NativeCapabilityGuard.RequireEmbeddedFieldLayout(size, offset, fieldSize, "Context"));

    [Fact]
    public void CurrentSdkModelsRetainerContextAndRenderFlagWithinTheirAllocations()
    {
        NativeCapabilityGuard.RequireEmbeddedFieldLayout(Marshal.SizeOf<AgentRetainer>(),
            checked((int)Marshal.OffsetOf<AgentRetainer>(nameof(AgentRetainer.InventoryContextEvent))),
            Marshal.SizeOf<AgentInventoryContext.InventoryContextEvent>(), "Retainer context");
        var renderOffset = typeof(Manager).GetField(nameof(Manager.Is3DRenderingDisabled))!
            .GetCustomAttribute<FieldOffsetAttribute>()!.Value;
        NativeCapabilityGuard.RequireEmbeddedFieldLayout(Unsafe.SizeOf<Manager>(), renderOffset, 1, "Render flag");
    }
}
