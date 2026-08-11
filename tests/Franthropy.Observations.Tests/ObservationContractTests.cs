using Franthropy.Observations.V1;

namespace Franthropy.Observations.Tests;

public sealed class ObservationContractTests
{
    [Fact]
    public void Capture_session_tags_only_the_exact_owner_and_retainer_while_owned()
    {
        var path = Path.Combine(Path.GetTempPath(), $"franthropy-capture-session-{Guid.NewGuid():N}.json");
        var requester = new ObservationCaptureSessionRegistry(path);
        var collector = new ObservationCaptureSessionRegistry(path);
        var owner = new ObservationOwner(10, 20);
        var exact = new ObservationScope(owner, ObservationSubject.Retainer(30, owner), ObservationContainerKind.RetainerInventory);
        var other = new ObservationScope(owner, ObservationSubject.Retainer(31, owner), ObservationContainerKind.RetainerInventory);

        try
        {
            string sessionId;
            using (var session = requester.Begin(owner, 30))
            {
                sessionId = session.SessionId;
                Assert.Equal(sessionId, collector.Resolve(exact));
                Assert.Empty(collector.Resolve(other));
                Assert.Throws<InvalidOperationException>(() => collector.Begin(owner, 30));
            }

            Assert.Empty(collector.Resolve(exact));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Assigned_roster_keeps_classless_retainers_with_unknown_ui_accessibility()
    {
        var owner = new ObservationOwner(100, 74);
        var roster = new RetainerRosterPayload(
        [
            new RetainerRosterObservation(200, "Classless", 74, 8, null, 0, 0, 0, false),
        ]);
        var observation = new ObservationEnvelope(
            new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.RetainerRoster),
            Capture(),
            ObservationPayload.Create(
                ObservationPayloadContracts.RetainerRoster,
                ObservationPayloadContracts.Version,
                roster));

        var result = ObservationValidator.Validate(observation);

        Assert.True(result.IsAuthoritative);
        var retainer = Assert.Single(observation.Payload!.Deserialize<RetainerRosterPayload>(
            ObservationPayloadContracts.RetainerRoster,
            ObservationPayloadContracts.Version).Retainers);
        Assert.Equal((byte)0, retainer.ClassJobId);
        Assert.Null(retainer.IsUiAccessible);
        Assert.False(retainer.IsGameAvailable);
        Assert.Equal(8, retainer.DisplayOrder);
    }

    [Fact]
    public void Zero_gil_is_authoritative_retainer_evidence()
    {
        var owner = new ObservationOwner(100, 74);
        var observation = new ObservationEnvelope(
            new ObservationScope(owner, ObservationSubject.Retainer(200, owner), ObservationContainerKind.RetainerGil),
            Capture(),
            ObservationPayload.Create(
                ObservationPayloadContracts.RetainerGil,
                ObservationPayloadContracts.Version,
                new RetainerGilPayload(0)));

        var result = ObservationValidator.Validate(observation);

        Assert.True(result.IsAuthoritative);
    }

    [Fact]
    public void Complete_empty_listing_capture_is_authoritative()
    {
        var observation = CreateListingObservation([], ObservationEvidence.CompleteAvailable);

        var result = ObservationValidator.Validate(observation);

        Assert.True(result.IsAuthoritative);
        Assert.Equal(ObservationValidationCode.Accepted, result.Code);
        Assert.Empty(observation.Payload!.Deserialize<RetainerMarketListingsPayload>(
            ObservationPayloadContracts.RetainerMarketListings,
            ObservationPayloadContracts.Version).Listings);
    }

    [Fact]
    public void Unavailable_listing_capture_preserves_prior_state()
    {
        var evidence = ObservationEvidence.CompleteAvailable with
        {
            Availability = ObservationAvailability.Transitioning,
            ContainerLoaded = false,
        };

        var result = ObservationValidator.Validate(CreateListingObservation(null, evidence));

        Assert.Equal(ObservationValidationStatus.Unavailable, result.Status);
        Assert.Equal(ObservationValidationCode.EvidenceUnavailable, result.Code);
    }

    [Fact]
    public void Partial_listing_capture_is_not_authoritative()
    {
        var evidence = ObservationEvidence.CompleteAvailable with
        {
            Completeness = ObservationCompleteness.Partial,
        };

        var result = ObservationValidator.Validate(CreateListingObservation([], evidence));

        Assert.Equal(ObservationValidationStatus.Partial, result.Status);
        Assert.Equal(ObservationValidationCode.EvidencePartial, result.Code);
    }

    [Fact]
    public void Retainer_owner_mismatch_is_rejected()
    {
        var observation = CreateListingObservation([], ObservationEvidence.CompleteAvailable);
        observation = observation with
        {
            Scope = observation.Scope with
            {
                Subject = observation.Scope.Subject with { OwnerLocalContentId = 999 },
            },
        };

        var result = ObservationValidator.Validate(observation);

        Assert.Equal(ObservationValidationStatus.Invalid, result.Status);
        Assert.Equal(ObservationValidationCode.OwnerMismatch, result.Code);
    }

    [Fact]
    public void Payload_contract_mismatch_fails_explicitly()
    {
        var payload = ObservationPayload.Create(ObservationPayloadContracts.PlayerInventory, 1, Array.Empty<InventoryItemObservation>());

        Assert.Throws<ObservationPayloadContractException>(() =>
            payload.Deserialize<IReadOnlyList<RetainerMarketListingObservation>>(
                ObservationPayloadContracts.RetainerMarketListings,
                1));
    }

    [Fact]
    public void Duplicate_listing_slots_are_rejected_before_persistence()
    {
        var observation = CreateListingObservation(
            [
                new RetainerMarketListingObservation(0, 100, 1, 10, false),
                new RetainerMarketListingObservation(0, 200, 1, 20, false),
            ],
            ObservationEvidence.CompleteAvailable);

        var result = ObservationValidator.Validate(observation);

        Assert.Equal(ObservationValidationStatus.Invalid, result.Status);
        Assert.Equal(ObservationValidationCode.PayloadInvalid, result.Code);
    }

    private static ObservationEnvelope CreateListingObservation(
        IReadOnlyList<RetainerMarketListingObservation>? rows,
        ObservationEvidence evidence)
    {
        var owner = new ObservationOwner(100, 74);
        return new ObservationEnvelope(
            new ObservationScope(
                owner,
                ObservationSubject.Retainer(200, owner),
                ObservationContainerKind.RetainerMarketListings),
            Capture(evidence),
            rows is null
                ? null
                : ObservationPayload.Create(
                    ObservationPayloadContracts.RetainerMarketListings,
                    ObservationPayloadContracts.Version,
                    new RetainerMarketListingsPayload(rows)));
    }

    private static ObservationCapture Capture(ObservationEvidence? evidence = null) => new(
        1,
        new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
        new ObservationProvenance("TestPlugin", "instance", "1.0.0", "2026.07.31.0000.0000"),
        evidence ?? ObservationEvidence.CompleteAvailable);
}
