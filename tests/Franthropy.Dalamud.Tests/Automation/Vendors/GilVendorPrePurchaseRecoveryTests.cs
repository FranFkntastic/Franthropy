using Franthropy.Dalamud.Automation.Vendors.Coordination;

namespace Franthropy.Dalamud.Tests.Automation.Vendors;

public sealed class GilVendorPrePurchaseRecoveryTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Owned_navigation_restarts_only_after_progress_stalls()
    {
        var recovery = CreateRecovery();

        Assert.Equal(
            GilVendorNavigationSubmissionDecision.Initial,
            recovery.ClassifyNavigationSubmission());
        recovery.RecordNavigationSubmission(StartedAt, 100f);
        Assert.Equal(
            GilVendorOwnedNavigationDecision.Continue,
            recovery.ObserveOwnedNavigation(StartedAt.AddSeconds(9), 100f));
        Assert.Equal(
            GilVendorOwnedNavigationDecision.Restart,
            recovery.ObserveOwnedNavigation(StartedAt.AddSeconds(10), 100f));
        Assert.Equal(
            GilVendorNavigationSubmissionDecision.Recovery,
            recovery.ClassifyNavigationSubmission());
        recovery.RecordNavigationSubmission(StartedAt.AddSeconds(11), 100f);

        Assert.Equal(
            GilVendorOwnedNavigationDecision.Continue,
            recovery.ObserveOwnedNavigation(StartedAt.AddSeconds(18), 98.9f));
        Assert.Equal(
            GilVendorOwnedNavigationDecision.Continue,
            recovery.ObserveOwnedNavigation(StartedAt.AddSeconds(27), 98.9f));
        Assert.Equal(
            GilVendorOwnedNavigationDecision.Restart,
            recovery.ObserveOwnedNavigation(StartedAt.AddSeconds(28), 98.9f));
    }

    [Fact]
    public void Owned_navigation_exhausts_after_two_bounded_restarts()
    {
        var recovery = CreateRecovery();
        recovery.RecordNavigationSubmission(StartedAt, 100f);

        Assert.Equal(
            GilVendorOwnedNavigationDecision.Restart,
            recovery.ObserveOwnedNavigation(StartedAt.AddSeconds(10), 100f));
        recovery.RecordNavigationSubmission(StartedAt.AddSeconds(11), 100f);
        Assert.Equal(
            GilVendorOwnedNavigationDecision.Restart,
            recovery.ObserveOwnedNavigation(StartedAt.AddSeconds(21), 100f));
        recovery.RecordNavigationSubmission(StartedAt.AddSeconds(22), 100f);

        Assert.Equal(
            GilVendorOwnedNavigationDecision.Exhausted,
            recovery.ObserveOwnedNavigation(StartedAt.AddSeconds(32), 100f));
    }

    [Fact]
    public void Unexpected_route_completion_consumes_the_same_recovery_budget()
    {
        var recovery = CreateRecovery();

        Assert.Equal(GilVendorNavigationSubmissionDecision.Initial, recovery.ClassifyNavigationSubmission());
        recovery.RecordNavigationSubmission(StartedAt, 100f);
        Assert.Equal(GilVendorNavigationSubmissionDecision.Recovery, recovery.ClassifyNavigationSubmission());
        recovery.RecordNavigationSubmission(StartedAt.AddSeconds(1), 90f);
        Assert.Equal(GilVendorNavigationSubmissionDecision.Recovery, recovery.ClassifyNavigationSubmission());
        recovery.RecordNavigationSubmission(StartedAt.AddSeconds(2), 80f);
        Assert.Equal(GilVendorNavigationSubmissionDecision.Exhausted, recovery.ClassifyNavigationSubmission());
    }

    [Fact]
    public void Mismatched_menu_gets_grace_then_two_bounded_reinteractions()
    {
        var recovery = CreateRecovery();

        Assert.Equal(GilVendorMenuRecoveryDecision.Wait, recovery.ObserveMenu(StartedAt, menuPresented: true, advanced: false));
        Assert.Equal(GilVendorMenuRecoveryDecision.Wait, recovery.ObserveMenu(StartedAt.AddSeconds(1), menuPresented: true, advanced: false));
        Assert.Equal(GilVendorMenuRecoveryDecision.Reinteract, recovery.ObserveMenu(StartedAt.AddSeconds(2), menuPresented: true, advanced: false));
        Assert.Equal(GilVendorMenuRecoveryDecision.NotPresented, recovery.ObserveMenu(StartedAt.AddSeconds(3), menuPresented: false, advanced: false));
        Assert.Equal(GilVendorMenuRecoveryDecision.Wait, recovery.ObserveMenu(StartedAt.AddSeconds(4), menuPresented: true, advanced: false));
        Assert.Equal(GilVendorMenuRecoveryDecision.Reinteract, recovery.ObserveMenu(StartedAt.AddSeconds(6), menuPresented: true, advanced: false));
        Assert.Equal(GilVendorMenuRecoveryDecision.Wait, recovery.ObserveMenu(StartedAt.AddSeconds(7), menuPresented: true, advanced: false));
        Assert.Equal(GilVendorMenuRecoveryDecision.Exhausted, recovery.ObserveMenu(StartedAt.AddSeconds(9), menuPresented: true, advanced: false));
    }

    [Fact]
    public void Successful_menu_advance_resets_the_reinteraction_budget()
    {
        var recovery = CreateRecovery();
        recovery.ObserveMenu(StartedAt, menuPresented: true, advanced: false);
        recovery.ObserveMenu(StartedAt.AddSeconds(2), menuPresented: true, advanced: false);

        Assert.Equal(
            GilVendorMenuRecoveryDecision.Advanced,
            recovery.ObserveMenu(StartedAt.AddSeconds(3), menuPresented: true, advanced: true));
        Assert.Equal(0, recovery.MenuReinteractionCount);
        Assert.Equal(
            GilVendorMenuRecoveryDecision.Wait,
            recovery.ObserveMenu(StartedAt.AddSeconds(4), menuPresented: true, advanced: false));
    }

    private static GilVendorPrePurchaseRecovery CreateRecovery() => new(
        navigationStallTimeout: TimeSpan.FromSeconds(10),
        menuMismatchGrace: TimeSpan.FromSeconds(2),
        navigationProgressThreshold: 1f,
        maximumNavigationRecoveries: 2,
        maximumMenuReinteractions: 2);
}
