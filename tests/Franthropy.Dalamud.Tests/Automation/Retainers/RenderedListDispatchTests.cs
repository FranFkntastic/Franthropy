using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.Automation.Retainers;

public sealed unsafe class RenderedListDispatchTests
{
    // A native-shaped vf44 fixture: delivery can occur without Handled being set.
    // Keep observations on the fixture, not static fields shared by parallel tests.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DispatchUsesMouseInputAndDoesNotInterpretHandledAsAcknowledgement(bool handled, bool doubleClick)
    {
        void** vtable = stackalloc void*[45];
        vtable[44] = (void*)(delegate* unmanaged<AtkComponentList*, AtkEventType, AtkEvent*, AtkEventData*, uint, byte, byte>)&Receive;
        var fixture = new Fixture { ReturnHandled = handled ? (byte)1 : (byte)0 };
        *(void***)&fixture.List = vtable;

        var result = DalamudRenderedUiTextActionDispatcher.DispatchVerifiedListRow(
            &fixture.List, doubleClick ? "RetainerList" : "SelectString", 3, doubleClick);

        Assert.True(fixture.Delivered);
        Assert.True(fixture.ZeroMousePayload);
        Assert.True(fixture.NullSourceEvent);
        Assert.Equal((byte)0, fixture.MarkSourceHandled);
        Assert.Equal((uint)3, fixture.Index);
        Assert.Equal(doubleClick ? AtkEventType.ListItemDoubleClick : AtkEventType.ListItemClick, fixture.EventType);
        Assert.True(result.Success);
        Assert.Equal("RenderedListRowActivationDispatched", result.Code);
        Assert.Contains("must verify", result.Message);
    }

    [UnmanagedCallersOnly]
    private static byte Receive(AtkComponentList* list, AtkEventType eventType, AtkEvent* source,
        AtkEventData* data, uint index, byte markSourceHandled)
    {
        var fixture = (Fixture*)list;
        fixture->Delivered = true;
        fixture->NullSourceEvent = source == null;
        fixture->ZeroMousePayload = true;
        for (var i = 0; i < sizeof(AtkEventData); i++)
            fixture->ZeroMousePayload &= ((byte*)data)[i] == 0;
        fixture->EventType = eventType;
        fixture->Index = index;
        fixture->MarkSourceHandled = markSourceHandled;
        return fixture->ReturnHandled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Fixture
    {
        public AtkComponentList List;
        public byte ReturnHandled;
        public bool Delivered;
        public bool ZeroMousePayload;
        public bool NullSourceEvent;
        public byte MarkSourceHandled;
        public uint Index;
        public AtkEventType EventType;
    }
}
