using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.PeerLink;

internal sealed class PresenceStore
{
    private readonly string directory;
    private readonly string application;
    internal PeerPresence Self { get; private set; }
    private string PathFor(Guid id) => Path.Combine(directory, $"{id:N}.json");
    internal static string Pipe(Guid id) => $"Franthropy.PeerLink.{id:N}";

    internal PresenceStore(string root, string application, JsonElement identity)
    {
        this.application = application;
        directory = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(application))));
        Directory.CreateDirectory(directory);
        using var process = Process.GetCurrentProcess();
        var id = Guid.NewGuid();
        Self = new(id, process.Id, process.StartTime.ToUniversalTime().Ticks, application, 1, Pipe(id), identity.Clone());
    }

    internal byte[] LoadKey()
    {
        var path = Path.Combine(directory, "key.dpapi");
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                File.WriteAllText(temporary, AgentBridgeDataProtection.ProtectToken(token, application));
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { } // Another complete atomic publication won.
            }
            finally { File.Delete(temporary); }
        }
        return Convert.FromBase64String(AgentBridgeDataProtection.UnprotectToken(File.ReadAllText(path), application));
    }

    internal void Publish(JsonElement identity)
    {
        var updated = Self with { Identity = identity.Clone() };
        var path = PathFor(Self.InstanceId);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(updated));
            File.Move(temporary, path, true);
            Self = updated;
        }
        finally { File.Delete(temporary); }
    }

    internal List<PeerPresence> Discover(Action<string> warn)
    {
        List<PeerPresence> peers = [];
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var p = JsonSerializer.Deserialize<PeerPresence>(file);
                if (p == null || p.InstanceId == Self.InstanceId || p.Application != application ||
                    PathFor(p.InstanceId) != path || p.PipeName != Pipe(p.InstanceId)) continue;
                try
                {
                    using var process = Process.GetProcessById(p.ProcessId);
                    if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == p.ProcessStartUtcTicks)
                    { peers.Add(p); continue; }
                }
                catch (ArgumentException) { } // PID no longer exists.
                File.Delete(path);
            }
            catch (FileNotFoundException) { } // Concurrent unload.
            catch (Exception e) { warn($"Presence {Path.GetFileName(path)}: {e.GetType().Name}"); }
        }
        return peers;
    }

    internal void Remove() => File.Delete(PathFor(Self.InstanceId));
}
