using Dalamud.Plugin.Ipc;
using Franthropy.Dalamud.Travel;

namespace Franthropy.Dalamud.Tests.Travel;

public sealed class DalamudLifestreamPropertyTravelTests
{
    [Fact]
    public void TrySubmit_SubmitsKnownPrivateEstateWhenIdle()
    {
        var busy = new FakeSubscriber<bool> { Function = () => false };
        var estate = new FakeSubscriber<bool?> { Function = () => true };
        var teleport = new FakeSubscriber<bool> { Function = () => true };
        var travel = new DalamudLifestreamPropertyTravel(busy, estate, teleport);

        var result = travel.TrySubmit();

        Assert.True(result.Submitted);
        Assert.Equal(1, teleport.FunctionInvocations);
    }

    [Fact]
    public void TrySubmit_FailsClosedWhenEstateAvailabilityIsUnknown()
    {
        var busy = new FakeSubscriber<bool> { Function = () => false };
        var estate = new FakeSubscriber<bool?> { Function = () => null };
        var teleport = new FakeSubscriber<bool> { Function = () => true };
        var travel = new DalamudLifestreamPropertyTravel(busy, estate, teleport);

        var result = travel.TrySubmit();

        Assert.Equal(PrivateEstateTravelState.Unavailable, result.State);
        Assert.Equal(0, teleport.FunctionInvocations);
    }

    private sealed class FakeSubscriber<TRet> : ICallGateSubscriber<TRet>
    {
        public Func<TRet>? Function { get; init; }
        public int FunctionInvocations { get; private set; }
        public bool HasAction => false;
        public bool HasFunction => true;
        public void Subscribe(Action action) { }
        public void Unsubscribe(Action action) { }
        public void InvokeAction() { }
        public TRet InvokeFunc()
        {
            FunctionInvocations++;
            return Function is null ? default! : Function();
        }
    }
}
