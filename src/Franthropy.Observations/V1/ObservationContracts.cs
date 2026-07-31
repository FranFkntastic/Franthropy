using System.Text.Json;

namespace Franthropy.Observations.V1;

public static class ObservationContract
{
    public static ObservationVersion Version { get; } = new(1, 0);
    public static ObservationVersion SchemaVersion { get; } = new(1, 0);
}

public readonly record struct ObservationVersion(int Major, int Minor) : IComparable<ObservationVersion>
{
    public int CompareTo(ObservationVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public override string ToString() => $"{Major}.{Minor}";
}

public sealed record ObservationOwner(ulong LocalContentId, uint HomeWorldId);

public enum ObservationSubjectKind
{
    Character,
    Retainer,
}

public sealed record ObservationSubject(
    ObservationSubjectKind Kind,
    ulong Id,
    ulong OwnerLocalContentId)
{
    public static ObservationSubject Character(ObservationOwner owner) =>
        new(ObservationSubjectKind.Character, owner.LocalContentId, owner.LocalContentId);

    public static ObservationSubject Retainer(ulong retainerId, ObservationOwner owner) =>
        new(ObservationSubjectKind.Retainer, retainerId, owner.LocalContentId);
}

public enum ObservationContainerKind
{
    PlayerInventory,
    RetainerRoster,
    RetainerInventory,
    RetainerMarketListings,
    Saddlebag,
}

public sealed record ObservationScope(
    ObservationOwner Owner,
    ObservationSubject Subject,
    ObservationContainerKind Container);

public enum ObservationAvailability
{
    Available,
    Unavailable,
    Transitioning,
}

public enum ObservationCompleteness
{
    Complete,
    Partial,
}

public sealed record ObservationEvidence(
    ObservationAvailability Availability,
    ObservationCompleteness Completeness,
    bool OwnerIdentityStable,
    bool SubjectOwnershipVerified,
    bool ContainerLoaded,
    bool ObservationWindowCoherent)
{
    public static ObservationEvidence CompleteAvailable { get; } = new(
        ObservationAvailability.Available,
        ObservationCompleteness.Complete,
        OwnerIdentityStable: true,
        SubjectOwnershipVerified: true,
        ContainerLoaded: true,
        ObservationWindowCoherent: true);
}

public sealed record ObservationProvenance(
    string PluginName,
    string PluginInstanceId,
    string FranthropyVersion,
    string GameBuild);

public sealed record ObservationCapture(
    long SourceRevision,
    DateTimeOffset ObservedAtUtc,
    ObservationProvenance Provenance,
    ObservationEvidence Evidence);

public sealed record ObservationPayload(string Contract, int Version, string Json)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static ObservationPayload Create<T>(string contract, int version, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return new ObservationPayload(contract, version, JsonSerializer.Serialize(value, SerializerOptions));
    }

    public T Deserialize<T>(string expectedContract, int expectedVersion)
    {
        if (!string.Equals(Contract, expectedContract, StringComparison.Ordinal) || Version != expectedVersion)
            throw new ObservationPayloadContractException(expectedContract, expectedVersion, Contract, Version);

        return JsonSerializer.Deserialize<T>(Json, SerializerOptions)
            ?? throw new InvalidDataException($"Observation payload '{Contract}' deserialized to null.");
    }
}

public sealed class ObservationPayloadContractException(
    string expectedContract,
    int expectedVersion,
    string actualContract,
    int actualVersion)
    : InvalidOperationException(
        $"Expected observation payload {expectedContract} v{expectedVersion}, but received {actualContract} v{actualVersion}.");

public sealed record ObservationEnvelope(
    ObservationScope Scope,
    ObservationCapture Capture,
    ObservationPayload? Payload);

public sealed record InventoryItemObservation(
    int ContainerId,
    int SlotIndex,
    uint ItemId,
    int Quantity,
    bool IsHighQuality);

public sealed record InventoryObservationPayload(
    IReadOnlyList<int> RequestedContainerIds,
    IReadOnlyList<int> ObservedContainerIds,
    IReadOnlyList<InventoryItemObservation> Items);

public sealed record RetainerRosterObservation(
    ulong RetainerId,
    string Name,
    uint WorldId);

public sealed record RetainerRosterPayload(IReadOnlyList<RetainerRosterObservation> Retainers);

public sealed record RetainerMarketListingObservation(
    int SlotIndex,
    uint ItemId,
    int Quantity,
    int UnitPrice,
    bool IsHighQuality);

public sealed record RetainerMarketListingsPayload(IReadOnlyList<RetainerMarketListingObservation> Listings);

public static class ObservationPayloadContracts
{
    public const string PlayerInventory = "franthropy.player-inventory.v1";
    public const string RetainerRoster = "franthropy.retainer-roster.v1";
    public const string RetainerInventory = "franthropy.retainer-inventory.v1";
    public const string RetainerMarketListings = "franthropy.retainer-market-listings.v1";
    public const string Saddlebag = "franthropy.saddlebag.v1";
    public const int Version = 1;
}
