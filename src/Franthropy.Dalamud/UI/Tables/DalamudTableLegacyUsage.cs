using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
#if NET10_0_OR_GREATER
using ECommons.Logging;
#endif

namespace Franthropy.Dalamud.UI.Tables;

internal static class DalamudTableLegacyUsage
{
    private static readonly ConcurrentDictionary<string, byte> WarnedApis = new(StringComparer.Ordinal);

    internal static void Warn(string api, Assembly consumerAssembly)
    {
        var consumerName = consumerAssembly.GetName().Name ?? consumerAssembly.FullName ?? "unknown consumer";
        if (!WarnedApis.TryAdd($"{consumerAssembly.FullName}:{api}", 0))
            return;

        var message =
            $"[Franthropy] DEPRECATED TABLE API: {consumerName} invoked {api}. " +
            "Configure table-level selection and migrate the row to DrawSaneRow; legacy rows bypass safe selection semantics.";
        var loggedThroughDalamud = false;
#if NET10_0_OR_GREATER
        try
        {
            PluginLog.Warning(message);
            loggedThroughDalamud = true;
        }
        catch
        {
            // ECommons may not be initialized by every consumer; the fallback remains reliable.
        }
#endif
        if (loggedThroughDalamud)
            return;

        Trace.TraceWarning(message);
        Console.Error.WriteLine(message);
    }
}
