namespace Franthropy.Dalamud.Automation.Retainers;

internal sealed class PositionFrameOneShotLatch
{
    private int consumed;

    public bool TryConsume() => Interlocked.Exchange(ref consumed, 1) == 0;
}
