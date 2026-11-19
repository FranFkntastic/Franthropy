using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Franthropy.PeerLink;

internal static class PipeProtocol
{
    internal const string Schema = "franthropy.peer";
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(10);
    internal static readonly JsonElement Empty = JsonSerializer.SerializeToElement<object?>(null);

    internal static NamedPipeServerStream Listen(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, -1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 8192, 8192, acl);
    }

    private static byte[] Proof(byte[] key, string role, Guid server, Guid client, byte[] a, byte[] b) =>
        HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"franthropy.peer/1/{role}/{server:N}/{client:N}/{Convert.ToHexString(a)}/{Convert.ToHexString(b)}"));

    internal static async Task<Guid> Accept(Stream pipe, byte[] key, Guid server, CancellationToken ct)
    {
        var a = RandomNumberGenerator.GetBytes(32);
        await pipe.WriteAsync(a, ct);
        var hello = new byte[80];
        await pipe.ReadExactlyAsync(hello, ct);
        var client = new Guid(hello.AsSpan(0,16));
        var b = hello[16..48];
        if (!CryptographicOperations.FixedTimeEquals(hello[48..], Proof(key,"client",server,client,a,b)))
        {
            await pipe.WriteAsync(new byte[]{0}, ct);
            throw new AuthenticationException("Client proof rejected.");
        }
        await pipe.WriteAsync(new byte[]{1}, ct);
        await pipe.WriteAsync(Proof(key,"server",server,client,a,b), ct);
        return client;
    }

    internal static async Task Authenticate(Stream pipe, byte[] key, Guid server, Guid client, CancellationToken ct)
    {
        var a = new byte[32];
        await pipe.ReadExactlyAsync(a, ct);
        var b = RandomNumberGenerator.GetBytes(32);
        byte[] hello = [..client.ToByteArray(), ..b, ..Proof(key,"client",server,client,a,b)];
        await pipe.WriteAsync(hello, ct);
        var status = new byte[1];
        await pipe.ReadExactlyAsync(status, ct);
        if (status[0] != 1) throw new AuthenticationException("Client proof rejected.");
        var mac = new byte[32];
        await pipe.ReadExactlyAsync(mac, ct);
        if (!CryptographicOperations.FixedTimeEquals(mac, Proof(key,"server",server,client,a,b)))
            throw new AuthenticationException("Server proof rejected.");
    }

    internal static async Task Write(Stream stream, PeerMessage message, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message);
        if (data.Length > 1024 * 1024) throw new InvalidDataException("Frame exceeds 1 MiB.");
        var frame = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, data.Length);
        data.CopyTo(frame,4);
        await stream.WriteAsync(frame, ct);
    }

    internal static async Task<PeerMessage> Read(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > 1024 * 1024) throw new InvalidDataException("Invalid frame size.");
        var data = new byte[size];
        await stream.ReadExactlyAsync(data, ct);
        return JsonSerializer.Deserialize<PeerMessage>(data) ?? throw new InvalidDataException("Empty envelope.");
    }
}
