namespace Franthropy.Observations.V1;

public enum ObservationValidationStatus
{
    Authoritative,
    Unavailable,
    Partial,
    Invalid,
}

public enum ObservationValidationCode
{
    Accepted,
    OwnerUndefined,
    SubjectUndefined,
    OwnerMismatch,
    ContainerSubjectMismatch,
    RevisionInvalid,
    TimestampInvalid,
    ProvenanceInvalid,
    PayloadMissing,
    PayloadInvalid,
    EvidenceUnavailable,
    EvidencePartial,
    EvidenceIncomplete,
}

public sealed record ObservationValidationResult(
    ObservationValidationStatus Status,
    ObservationValidationCode Code,
    string Message)
{
    public bool IsAuthoritative => Status == ObservationValidationStatus.Authoritative;
}

public static class ObservationValidator
{
    public static ObservationValidationResult Validate(ObservationEnvelope observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var scope = observation.Scope;
        var capture = observation.Capture;
        var evidence = capture.Evidence;

        if (scope.Owner.LocalContentId == 0 || scope.Owner.HomeWorldId == 0)
            return Invalid(ObservationValidationCode.OwnerUndefined, "The character owner identity is not stable and exact.");
        if (scope.Subject.Id == 0)
            return Invalid(ObservationValidationCode.SubjectUndefined, "The observation subject is undefined.");
        if (scope.Subject.OwnerLocalContentId != scope.Owner.LocalContentId ||
            scope.Subject.Kind == ObservationSubjectKind.Character && scope.Subject.Id != scope.Owner.LocalContentId ||
            !evidence.OwnerIdentityStable || !evidence.SubjectOwnershipVerified)
            return Invalid(ObservationValidationCode.OwnerMismatch, "The subject is not proven to belong to the exact observation owner.");
        if (!ContainerMatchesSubject(scope.Container, scope.Subject.Kind))
            return Invalid(ObservationValidationCode.ContainerSubjectMismatch, "The container kind does not belong to the declared subject kind.");
        if (capture.SourceRevision <= 0)
            return Invalid(ObservationValidationCode.RevisionInvalid, "Producer revisions must be positive and monotonic within one plugin instance and scope.");
        if (capture.ObservedAtUtc == default || capture.ObservedAtUtc.Offset != TimeSpan.Zero)
            return Invalid(ObservationValidationCode.TimestampInvalid, "Observation timestamps must be explicit UTC values.");
        if (string.IsNullOrWhiteSpace(capture.Provenance.PluginName) ||
            string.IsNullOrWhiteSpace(capture.Provenance.PluginInstanceId) ||
            string.IsNullOrWhiteSpace(capture.Provenance.FranthropyVersion) ||
            string.IsNullOrWhiteSpace(capture.Provenance.GameBuild))
            return Invalid(ObservationValidationCode.ProvenanceInvalid, "Observation provenance is incomplete.");

        if (evidence.Availability != ObservationAvailability.Available)
            return new ObservationValidationResult(
                ObservationValidationStatus.Unavailable,
                ObservationValidationCode.EvidenceUnavailable,
                "The container was unavailable or transitioning; prior truthful state must be preserved as stale.");

        if (evidence.Completeness != ObservationCompleteness.Complete)
            return new ObservationValidationResult(
                ObservationValidationStatus.Partial,
                ObservationValidationCode.EvidencePartial,
                "The scan was partial; it cannot replace trusted state.");

        if (!evidence.ContainerLoaded || !evidence.ObservationWindowCoherent)
            return Invalid(ObservationValidationCode.EvidenceIncomplete, "The container was not proven loaded and coherent.");
        if (observation.Payload is null)
            return Invalid(ObservationValidationCode.PayloadMissing, "A complete available observation requires a typed payload, including for an empty container.");
        var expectedPayloadContract = ExpectedPayloadContract(scope.Container);
        if (!string.Equals(observation.Payload.Contract, expectedPayloadContract, StringComparison.Ordinal) ||
            observation.Payload.Version != ObservationPayloadContracts.Version)
            return Invalid(
                ObservationValidationCode.PayloadMissing,
                $"Container {scope.Container} requires payload {expectedPayloadContract} v{ObservationPayloadContracts.Version}.");
        try
        {
            if (!PayloadIsValid(scope.Container, observation.Payload, out var payloadError))
                return Invalid(ObservationValidationCode.PayloadInvalid, payloadError);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or ObservationPayloadContractException)
        {
            return Invalid(ObservationValidationCode.PayloadInvalid, $"The typed observation payload is invalid: {ex.Message}");
        }

        return new ObservationValidationResult(
            ObservationValidationStatus.Authoritative,
            ObservationValidationCode.Accepted,
            "The observation is authoritative.");
    }

    private static bool ContainerMatchesSubject(ObservationContainerKind container, ObservationSubjectKind subject) =>
        container switch
        {
            ObservationContainerKind.PlayerInventory or
            ObservationContainerKind.RetainerRoster or
            ObservationContainerKind.Saddlebag => subject == ObservationSubjectKind.Character,
            ObservationContainerKind.RetainerInventory or
            ObservationContainerKind.RetainerMarketListings or
            ObservationContainerKind.RetainerGil => subject == ObservationSubjectKind.Retainer,
            _ => false,
        };

    private static string ExpectedPayloadContract(ObservationContainerKind container) =>
        container switch
        {
            ObservationContainerKind.PlayerInventory => ObservationPayloadContracts.PlayerInventory,
            ObservationContainerKind.RetainerRoster => ObservationPayloadContracts.RetainerRoster,
            ObservationContainerKind.RetainerInventory => ObservationPayloadContracts.RetainerInventory,
            ObservationContainerKind.RetainerMarketListings => ObservationPayloadContracts.RetainerMarketListings,
            ObservationContainerKind.RetainerGil => ObservationPayloadContracts.RetainerGil,
            ObservationContainerKind.Saddlebag => ObservationPayloadContracts.Saddlebag,
            _ => throw new ArgumentOutOfRangeException(nameof(container), container, null),
        };

    private static bool PayloadIsValid(
        ObservationContainerKind container,
        ObservationPayload payload,
        out string error)
    {
        switch (container)
        {
            case ObservationContainerKind.PlayerInventory:
            case ObservationContainerKind.RetainerInventory:
            case ObservationContainerKind.Saddlebag:
            {
                var inventory = payload.Deserialize<InventoryObservationPayload>(
                    ExpectedPayloadContract(container),
                    ObservationPayloadContracts.Version);
                var requested = inventory.RequestedContainerIds.ToHashSet();
                var observed = inventory.ObservedContainerIds.ToHashSet();
                if (requested.Count == 0 ||
                    requested.Count != inventory.RequestedContainerIds.Count ||
                    observed.Count != inventory.ObservedContainerIds.Count)
                {
                    error = "A complete inventory payload requires distinct requested container IDs.";
                    return false;
                }
                if (!requested.All(observed.Contains))
                {
                    error = "A complete inventory payload did not observe every requested container.";
                    return false;
                }
                if (inventory.Items.Any(item =>
                        item.ItemId == 0 || item.Quantity <= 0 || item.SlotIndex < 0 || !observed.Contains(item.ContainerId)))
                {
                    error = "An inventory row is missing exact item, quantity, slot, or observed-container identity.";
                    return false;
                }
                if (inventory.Items
                        .Select(item => (item.ContainerId, item.SlotIndex))
                        .Distinct()
                        .Count() != inventory.Items.Count)
                {
                    error = "An inventory payload contains more than one row for the same container slot.";
                    return false;
                }
                break;
            }
            case ObservationContainerKind.RetainerRoster:
            {
                var roster = payload.Deserialize<RetainerRosterPayload>(
                    ObservationPayloadContracts.RetainerRoster,
                    ObservationPayloadContracts.Version);
                if (roster.Retainers.Any(retainer => retainer.RetainerId == 0 || retainer.WorldId == 0 || string.IsNullOrWhiteSpace(retainer.Name)) ||
                    roster.Retainers.Select(retainer => retainer.RetainerId).Distinct().Count() != roster.Retainers.Count)
                {
                    error = "A retainer roster row is incomplete or duplicated.";
                    return false;
                }
                break;
            }
            case ObservationContainerKind.RetainerMarketListings:
            {
                var listings = payload.Deserialize<RetainerMarketListingsPayload>(
                    ObservationPayloadContracts.RetainerMarketListings,
                    ObservationPayloadContracts.Version);
                if (listings.Listings.Any(listing =>
                        listing.SlotIndex < 0 || listing.ItemId == 0 || listing.Quantity <= 0 || listing.UnitPrice <= 0) ||
                    listings.Listings.Select(listing => listing.SlotIndex).Distinct().Count() != listings.Listings.Count)
                {
                    error = "A retainer listing row is incomplete or duplicates a market slot.";
                    return false;
                }
                break;
            }
            case ObservationContainerKind.RetainerGil:
            {
                _ = payload.Deserialize<RetainerGilPayload>(
                    ObservationPayloadContracts.RetainerGil,
                    ObservationPayloadContracts.Version);
                break;
            }
            default:
                error = $"Unsupported observation container {container}.";
                return false;
        }

        error = string.Empty;
        return true;
    }

    private static ObservationValidationResult Invalid(ObservationValidationCode code, string message) =>
        new(ObservationValidationStatus.Invalid, code, message);
}
