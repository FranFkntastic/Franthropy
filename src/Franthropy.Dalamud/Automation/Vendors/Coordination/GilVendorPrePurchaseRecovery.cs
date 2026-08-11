namespace Franthropy.Dalamud.Automation.Vendors.Coordination;

internal sealed class GilVendorPrePurchaseRecovery
{
    private static readonly TimeSpan DefaultNavigationStallTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultMenuMismatchGrace = TimeSpan.FromSeconds(2);
    private const float DefaultNavigationProgressThreshold = 1f;
    private const int DefaultMaximumNavigationRecoveries = 2;
    private const int DefaultMaximumMenuReinteractions = 2;

    private readonly TimeSpan navigationStallTimeout;
    private readonly TimeSpan menuMismatchGrace;
    private readonly float navigationProgressThreshold;
    private readonly int maximumNavigationRecoveries;
    private readonly int maximumMenuReinteractions;
    private bool navigationWasSubmitted;
    private bool navigationRestartPending;
    private int navigationRecoveryCount;
    private float nearestNavigationDistance = float.PositiveInfinity;
    private DateTimeOffset navigationProgressObservedAt;
    private int menuReinteractionCount;
    private DateTimeOffset? menuMismatchObservedAt;

    public GilVendorPrePurchaseRecovery(
        TimeSpan? navigationStallTimeout = null,
        TimeSpan? menuMismatchGrace = null,
        float navigationProgressThreshold = DefaultNavigationProgressThreshold,
        int maximumNavigationRecoveries = DefaultMaximumNavigationRecoveries,
        int maximumMenuReinteractions = DefaultMaximumMenuReinteractions)
    {
        this.navigationStallTimeout = navigationStallTimeout ?? DefaultNavigationStallTimeout;
        this.menuMismatchGrace = menuMismatchGrace ?? DefaultMenuMismatchGrace;
        this.navigationProgressThreshold = navigationProgressThreshold;
        this.maximumNavigationRecoveries = maximumNavigationRecoveries;
        this.maximumMenuReinteractions = maximumMenuReinteractions;
    }

    public int NavigationRecoveryCount => navigationRecoveryCount;
    public int MaximumNavigationRecoveries => maximumNavigationRecoveries;
    public int MenuReinteractionCount => menuReinteractionCount;
    public int MaximumMenuReinteractions => maximumMenuReinteractions;

    public GilVendorNavigationSubmissionDecision ClassifyNavigationSubmission()
    {
        if (!navigationWasSubmitted)
            return GilVendorNavigationSubmissionDecision.Initial;

        return navigationRestartPending || navigationRecoveryCount < maximumNavigationRecoveries
            ? GilVendorNavigationSubmissionDecision.Recovery
            : GilVendorNavigationSubmissionDecision.Exhausted;
    }

    public void RecordNavigationSubmission(DateTimeOffset observedAt, float distance)
    {
        var decision = ClassifyNavigationSubmission();
        if (decision == GilVendorNavigationSubmissionDecision.Exhausted)
            throw new InvalidOperationException("The bounded navigation recovery budget is exhausted.");

        if (decision == GilVendorNavigationSubmissionDecision.Initial)
            navigationWasSubmitted = true;
        else if (!navigationRestartPending)
            navigationRecoveryCount++;

        navigationRestartPending = false;
        ObserveNavigationProgress(observedAt, distance);
    }

    public GilVendorOwnedNavigationDecision ObserveOwnedNavigation(
        DateTimeOffset observedAt,
        float distance)
    {
        if (!navigationWasSubmitted)
        {
            navigationWasSubmitted = true;
            ObserveNavigationProgress(observedAt, distance);
            return GilVendorOwnedNavigationDecision.Continue;
        }

        if (distance <= nearestNavigationDistance - navigationProgressThreshold)
        {
            ObserveNavigationProgress(observedAt, distance);
            return GilVendorOwnedNavigationDecision.Continue;
        }

        if (observedAt - navigationProgressObservedAt < navigationStallTimeout)
            return GilVendorOwnedNavigationDecision.Continue;

        if (navigationRecoveryCount >= maximumNavigationRecoveries)
            return GilVendorOwnedNavigationDecision.Exhausted;

        navigationRecoveryCount++;
        navigationRestartPending = true;
        ObserveNavigationProgress(observedAt, distance);
        return GilVendorOwnedNavigationDecision.Restart;
    }

    public GilVendorMenuRecoveryDecision ObserveMenu(
        DateTimeOffset observedAt,
        bool menuPresented,
        bool advanced)
    {
        if (!menuPresented)
        {
            menuMismatchObservedAt = null;
            return GilVendorMenuRecoveryDecision.NotPresented;
        }

        if (advanced)
        {
            ResetMenu();
            return GilVendorMenuRecoveryDecision.Advanced;
        }

        menuMismatchObservedAt ??= observedAt;
        if (observedAt - menuMismatchObservedAt.Value < menuMismatchGrace)
            return GilVendorMenuRecoveryDecision.Wait;

        if (menuReinteractionCount >= maximumMenuReinteractions)
            return GilVendorMenuRecoveryDecision.Exhausted;

        menuReinteractionCount++;
        menuMismatchObservedAt = null;
        return GilVendorMenuRecoveryDecision.Reinteract;
    }

    public void Reset()
    {
        ResetNavigation();
        ResetMenu();
    }

    public void ResetNavigation()
    {
        navigationWasSubmitted = false;
        navigationRestartPending = false;
        navigationRecoveryCount = 0;
        nearestNavigationDistance = float.PositiveInfinity;
        navigationProgressObservedAt = default;
    }

    public void ResetMenu()
    {
        menuReinteractionCount = 0;
        menuMismatchObservedAt = null;
    }

    private void ObserveNavigationProgress(DateTimeOffset observedAt, float distance)
    {
        navigationProgressObservedAt = observedAt;
        nearestNavigationDistance = distance;
    }
}

internal enum GilVendorNavigationSubmissionDecision
{
    Initial,
    Recovery,
    Exhausted,
}

internal enum GilVendorOwnedNavigationDecision
{
    Continue,
    Restart,
    Exhausted,
}

internal enum GilVendorMenuRecoveryDecision
{
    NotPresented,
    Advanced,
    Wait,
    Reinteract,
    Exhausted,
}
