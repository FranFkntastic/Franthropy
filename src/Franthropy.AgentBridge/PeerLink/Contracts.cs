using System.Text.Json;

namespace Franthropy.PeerLink;

public sealed record PeerPresence(Guid InstanceId, int ProcessId, long ProcessStartUtcTicks,
    string Application, int ProtocolVersion, string PipeName, JsonElement Identity);

public sealed record PeerMessage(string Schema, int ProtocolVersion, Guid MessageId, Guid CorrelationId,
    Guid SenderInstanceId, string Kind, DateTimeOffset TimestampUtc, JsonElement Payload);

public abstract record PeerResult
{
    public sealed record Delivered(JsonElement Response) : PeerResult;
    public sealed record Refused(string Code, string Reason) : PeerResult;
    public sealed record TimedOut(string Threshold, TimeSpan Limit) : PeerResult;
    public sealed record LinkLost(string Reason) : PeerResult;
    public sealed record Cancelled : PeerResult;
}

public sealed record PeerOptions(string Application,
    Func<Action, CancellationToken, Task> Dispatch,
    Func<PeerMessage, PeerResult> Handle,
    Action<string> Warning);

public enum PeerState { Connected, Lost, Recovered }
