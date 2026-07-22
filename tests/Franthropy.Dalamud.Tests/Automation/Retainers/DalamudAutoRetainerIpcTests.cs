using Dalamud.Plugin.Ipc;
using Franthropy.Dalamud.Automation.Retainers;

namespace Franthropy.Dalamud.Tests.Automation.Retainers;

public sealed class DalamudAutoRetainerIpcTests
{
    [Fact]
    public void Transport_ForwardsCallbacksAndCommandsThenUnsubscribes()
    {
        var channels = new Channels { Busy = { Function = () => true } };
        using var transport = channels.Create();
        var draws = 0;
        var additional = string.Empty;
        var ready = (Consumer: string.Empty, Retainer: string.Empty);
        transport.Register(new(
            () => draws++,
            name => additional = name,
            (consumer, name) => ready = (consumer, name)));

        Assert.True(transport.IsAvailable);
        Assert.True(transport.IsBusy);
        channels.Draw.Publish();
        channels.Additional.Publish("Alpha");
        channels.Ready.Publish("Quartermaster", "Beta");
        transport.QueueRetainerListTask("Quartermaster");
        transport.RequestPostprocess("Quartermaster");
        transport.FinishPostprocess();

        Assert.Equal(1, draws);
        Assert.Equal("Alpha", additional);
        Assert.Equal(("Quartermaster", "Beta"), ready);
        Assert.Equal(["Quartermaster"], channels.CustomTask.ActionArguments);
        Assert.Equal(["Quartermaster"], channels.Request.ActionArguments);
        Assert.Equal(1, channels.Finish.ActionInvocations);

        transport.Dispose();
        Assert.Empty(channels.Draw.Subscriptions);
        Assert.Empty(channels.Additional.Subscriptions);
        Assert.Empty(channels.Ready.Subscriptions);
    }

    [Fact]
    public void Register_RollsBackEveryEarlierSubscription()
    {
        var channels = new Channels();
        channels.Ready.ThrowOnSubscribe = true;
        using var transport = channels.Create();

        Assert.Throws<InvalidOperationException>(() => transport.Register(new(() => { }, _ => { }, (_, _) => { })));

        Assert.Empty(channels.Draw.Subscriptions);
        Assert.Empty(channels.Additional.Subscriptions);
        Assert.Empty(channels.Ready.Subscriptions);
    }

    private sealed class Channels
    {
        public FakeSubscriber<object> Init { get; } = new();
        public FakeSubscriber<bool> Busy { get; } = new();
        public FakeSubscriber<object> Draw { get; } = new();
        public FakeSubscriber<string, object> CustomTask { get; } = new();
        public FakeSubscriber<string, object> Additional { get; } = new();
        public FakeSubscriber<string, object> Request { get; } = new();
        public FakeSubscriber<string, string, object> Ready { get; } = new();
        public FakeSubscriber<object> Finish { get; } = new();

        public DalamudAutoRetainerIpc Create() => new(Init, Busy, Draw, CustomTask, Additional, Request, Ready, Finish);
    }

    private sealed class FakeSubscriber<TRet> : ICallGateSubscriber<TRet>
    {
        public List<Action> Subscriptions { get; } = [];
        public Func<TRet>? Function { get; set; }
        public bool ThrowOnSubscribe { get; set; }
        public int ActionInvocations { get; private set; }
        public bool HasAction => true;
        public bool HasFunction => Function is not null;

        public void Subscribe(Action action)
        {
            if (ThrowOnSubscribe)
                throw new InvalidOperationException("Subscription failed.");
            Subscriptions.Add(action);
        }

        public void Unsubscribe(Action action) => Subscriptions.Remove(action);
        public void InvokeAction() => ActionInvocations++;
        public TRet InvokeFunc() => Function is null ? default! : Function();
        public void Publish()
        {
            foreach (var action in Subscriptions.ToArray())
                action();
        }
    }

    private sealed class FakeSubscriber<T1, TRet> : ICallGateSubscriber<T1, TRet>
    {
        public List<Action<T1>> Subscriptions { get; } = [];
        public List<T1> ActionArguments { get; } = [];
        public bool HasAction => true;
        public bool HasFunction => false;

        public void Subscribe(Action<T1> action) => Subscriptions.Add(action);
        public void Unsubscribe(Action<T1> action) => Subscriptions.Remove(action);
        public void InvokeAction(T1 arg1) => ActionArguments.Add(arg1);
        public TRet InvokeFunc(T1 arg1) => default!;
        public void Publish(T1 arg1)
        {
            foreach (var action in Subscriptions.ToArray())
                action(arg1);
        }
    }

    private sealed class FakeSubscriber<T1, T2, TRet> : ICallGateSubscriber<T1, T2, TRet>
    {
        public List<Action<T1, T2>> Subscriptions { get; } = [];
        public bool ThrowOnSubscribe { get; set; }
        public bool HasAction => true;
        public bool HasFunction => false;

        public void Subscribe(Action<T1, T2> action)
        {
            if (ThrowOnSubscribe)
                throw new InvalidOperationException("Subscription failed.");
            Subscriptions.Add(action);
        }

        public void Unsubscribe(Action<T1, T2> action) => Subscriptions.Remove(action);
        public void InvokeAction(T1 arg1, T2 arg2) { }
        public TRet InvokeFunc(T1 arg1, T2 arg2) => default!;
        public void Publish(T1 arg1, T2 arg2)
        {
            foreach (var action in Subscriptions.ToArray())
                action(arg1, arg2);
        }
    }
}
