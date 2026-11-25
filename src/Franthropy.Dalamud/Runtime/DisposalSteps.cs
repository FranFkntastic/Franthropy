namespace Franthropy.Dalamud.Runtime;

/// <summary>
/// Runs a chain of disposal or restore steps so that one throwing step never skips the steps after it.
/// </summary>
public static class DisposalSteps
{
    /// <summary>
    /// Runs every step in order, each in its own try/catch, reporting each failure to <paramref name="onFailure"/>
    /// with the step name (log it at Warning). Returns the number of steps that threw.
    /// </summary>
    public static int DisposeAll(Action<string, Exception> onFailure, params (string Name, Action Dispose)[] steps)
    {
        ArgumentNullException.ThrowIfNull(onFailure);
        ArgumentNullException.ThrowIfNull(steps);
        var failed = 0;
        foreach (var (name, dispose) in steps)
        {
            try
            {
                dispose();
            }
            catch (Exception exception)
            {
                failed++;
                try
                {
                    onFailure(name, exception);
                }
                catch
                {
                    // Reporting must never stop the remaining steps.
                }
            }
        }

        return failed;
    }
}
