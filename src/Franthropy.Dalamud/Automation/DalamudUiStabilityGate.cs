namespace Franthropy.Dalamud.Automation;

/// <summary>
/// Requires a UI observation to remain continuously ready across several framework frames
/// before a transaction submits an input. Transient one-frame addon visibility is not a
/// reliable indication that the game's UI event graph is ready to accept that input.
/// </summary>
public sealed class DalamudUiStabilityGate
{
    public DalamudUiStabilityGate(int requiredConsecutiveFrames)
    {
        if (requiredConsecutiveFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredConsecutiveFrames));
        RequiredConsecutiveFrames = requiredConsecutiveFrames;
    }

    public int RequiredConsecutiveFrames { get; }
    public int ObservedConsecutiveFrames { get; private set; }

    public bool Observe(bool ready)
    {
        if (!ready)
        {
            Reset();
            return false;
        }

        if (ObservedConsecutiveFrames < RequiredConsecutiveFrames)
            ObservedConsecutiveFrames++;
        return ObservedConsecutiveFrames >= RequiredConsecutiveFrames;
    }

    public void Reset() => ObservedConsecutiveFrames = 0;
}
