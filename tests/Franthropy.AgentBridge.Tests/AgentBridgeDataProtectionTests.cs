using Franthropy.Dalamud.AgentBridge;
using System.Security.Cryptography;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class AgentBridgeDataProtectionTests
{
    [Fact]
    public void Token_round_trips_only_for_its_plugin_instance()
    {
        const string token = "local-bridge-token";
        var protectedToken = AgentBridgeDataProtection.ProtectToken(token, "instance-a");

        Assert.NotEqual(token, protectedToken);
        Assert.Equal(token, AgentBridgeDataProtection.UnprotectToken(protectedToken, "instance-a"));
        Assert.Throws<CryptographicException>(() => AgentBridgeDataProtection.UnprotectToken(protectedToken, "instance-b"));
    }

    [Fact]
    public void Capture_bytes_round_trip_only_for_its_plugin_instance()
    {
        var source = "rendered-png-bytes"u8.ToArray();
        var protectedBytes = AgentBridgeDataProtection.ProtectBytes(source, "instance-a");
        var recovered = AgentBridgeDataProtection.UnprotectBytes(protectedBytes, "instance-a");
        try
        {
            Assert.Equal(source, recovered);
            Assert.Throws<CryptographicException>(() => AgentBridgeDataProtection.UnprotectBytes(protectedBytes, "instance-b"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(recovered);
        }
    }
}
