using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Application.Network;

namespace Franthropy.Dalamud.Automation.Retainers;

/// <summary>
/// Passively observes one stock StartTalkEvent packet for an exact live actor/event pair.
/// The matching packet is never suppressed, replaced, retried, or otherwise altered.
/// </summary>
public sealed unsafe class DalamudTalkEventPacketTransport : IDisposable
{
    private const int PacketPayloadOffset = 0x20;
    private const int MinimumTalkPayloadLength = 12;
    private const int MaximumCapturedPayloadLength = 256;

    private readonly Hook<ZoneClient.Delegates.SendPacket> sendPacketHook;
    private CaptureTarget? captureTarget;
    private int packetsObservedWhileArmed;
    private int sizeEligiblePacketsObserved;
    private TalkEventPacketTransportObservation observation =
        new(TalkEventPacketTransportState.Idle, 0, false, false, 0, 0, "The talk-event observer is idle.");
    private bool disposed;

    public DalamudTalkEventPacketTransport(IGameInteropProvider interopProvider)
    {
        sendPacketHook = interopProvider.HookFromAddress<ZoneClient.Delegates.SendPacket>(
            (nint)ZoneClient.MemberFunctionPointers.SendPacket,
            SendPacketDetour);
        sendPacketHook.Enable();
    }

    public TalkEventPacketTransportObservation ArmPassThrough(ulong actorId, uint eventId)
    {
        if (disposed)
            return Failed("The talk-event packet observer has been disposed.");
        if (captureTarget is not null)
            return Failed("A talk-event packet observation is already armed.");

        Interlocked.Exchange(ref packetsObservedWhileArmed, 0);
        Interlocked.Exchange(ref sizeEligiblePacketsObserved, 0);
        captureTarget = new(actorId, eventId);
        observation = new(
            TalkEventPacketTransportState.AwaitingBuilderPacket,
            0,
            false,
            false,
            0,
            0,
            "Armed a passive observer for the stock StartTalkEvent packet.");
        return observation;
    }

    private bool SendPacketDetour(
        ZoneClient* zoneClient,
        nint packet,
        uint argument3,
        uint argument4,
        bool argument5)
    {
        if (captureTarget is not null)
            Interlocked.Increment(ref packetsObservedWhileArmed);

        if (TryObserve(packet) is { } opcode)
        {
            captureTarget = null;
            var accepted = sendPacketHook.Original(zoneClient, packet, argument3, argument4, argument5);
            observation = new(
                accepted ? TalkEventPacketTransportState.StockPacketSent : TalkEventPacketTransportState.Failed,
                opcode,
                false,
                false,
                Volatile.Read(ref packetsObservedWhileArmed),
                Volatile.Read(ref sizeEligiblePacketsObserved),
                accepted
                    ? $"Observed and passed through one stock StartTalkEvent packet unchanged (opcode 0x{opcode:X})."
                    : $"Observed the stock StartTalkEvent packet, but the zone send primitive returned false (opcode 0x{opcode:X}).");
            return accepted;
        }

        return sendPacketHook.Original(zoneClient, packet, argument3, argument4, argument5);
    }

    private uint? TryObserve(nint packet)
    {
        if (captureTarget is not { } target || packet == 0)
            return null;

        var packetPointer = (byte*)packet;
        var encodedSize = *(ulong*)(packetPointer + 8);
        if (encodedSize < 0x10 + MinimumTalkPayloadLength ||
            encodedSize > 0x10 + MaximumCapturedPayloadLength)
        {
            return null;
        }

        Interlocked.Increment(ref sizeEligiblePacketsObserved);
        var payloadPointer = packetPointer + PacketPayloadOffset;
        return *(ulong*)payloadPointer == target.ActorId &&
               *(uint*)(payloadPointer + 8) == target.EventId
            ? *(uint*)packetPointer
            : null;
    }

    public TalkEventPacketTransportObservation Observe() =>
        observation with
        {
            PacketsObservedWhileArmed = Volatile.Read(ref packetsObservedWhileArmed),
            SizeEligiblePacketsObserved = Volatile.Read(ref sizeEligiblePacketsObserved),
        };

    public void CancelPending(string reason)
    {
        var wasPending = observation.State == TalkEventPacketTransportState.AwaitingBuilderPacket;
        captureTarget = null;
        if (wasPending)
        {
            observation = Failed(
                $"{reason} Observed {Volatile.Read(ref packetsObservedWhileArmed)} outbound packet(s) while armed, " +
                $"{Volatile.Read(ref sizeEligiblePacketsObserved)} with a compatible envelope size.");
        }
    }

    private TalkEventPacketTransportObservation Failed(string message)
    {
        observation = new(
            TalkEventPacketTransportState.Failed,
            0,
            false,
            false,
            Volatile.Read(ref packetsObservedWhileArmed),
            Volatile.Read(ref sizeEligiblePacketsObserved),
            message);
        return observation;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        CancelPending("The talk-event packet observer was disposed.");
        sendPacketHook.Dispose();
    }

    private sealed record CaptureTarget(ulong ActorId, uint EventId);
}

public enum TalkEventPacketTransportState
{
    Idle,
    AwaitingBuilderPacket,
    StockPacketSent,
    Failed,
}

public sealed record TalkEventPacketTransportObservation(
    TalkEventPacketTransportState State,
    uint Opcode,
    bool BuilderPacketSuppressed,
    bool ConstructedPacket,
    int PacketsObservedWhileArmed,
    int SizeEligiblePacketsObserved,
    string Message)
{
    public bool Pending => State == TalkEventPacketTransportState.AwaitingBuilderPacket;
    public bool Sent => State == TalkEventPacketTransportState.StockPacketSent;
}
