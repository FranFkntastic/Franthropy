using Dalamud.Interface.Windowing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Franthropy.Dalamud.AgentBridge;

/// <summary>
/// Performs a deliberately shallow, read-only search for shared Dalamud window abstractions.
/// It never invokes plugin methods, reads plugin properties, or retains reflected objects.
/// </summary>
public sealed class ReflectedPluginWindowSurfaceInspector
{
    private const int DefaultMaximumFields = 128;
    private const int DefaultMaximumWindows = 64;
    private const int DefaultMaximumObjects = 32;
    private const int DefaultMaximumDepth = 2;

    public IReadOnlyList<AgentBridgePluginSurfaceDescriptor> Inspect(
        object pluginInstance,
        string pluginInternalName,
        string pluginName,
        string runtimeInstanceId,
        int maximumFields = DefaultMaximumFields,
        int maximumWindows = DefaultMaximumWindows)
    {
        ArgumentNullException.ThrowIfNull(pluginInstance);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginInternalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        if (maximumFields is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumFields));
        if (maximumWindows is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maximumWindows));

        var candidates = EnumerateWindows(pluginInstance, maximumFields, maximumWindows);
        var surfaces = new List<AgentBridgePluginSurfaceDescriptor>();
        var ordinal = 0;
        foreach (var candidate in candidates)
        {
            var window = candidate.Window;
            try
            {
                var windowName = window.WindowName;
                var windowNamespace = window.Namespace ?? candidate.SystemNamespace;
                var stableName = StripImGuiId(windowName);
                surfaces.Add(new AgentBridgePluginSurfaceDescriptor(
                    StableId(pluginInternalName, windowNamespace, windowName, ordinal),
                    pluginInternalName,
                    pluginName,
                    string.IsNullOrWhiteSpace(stableName) ? "Unnamed window" : stableName,
                    AgentBridgePluginSurfaceKind.Window,
                    AgentBridgeSurfaceProvenance.ReflectedWindowSystem,
                    AgentBridgeSurfaceAuthority.ReversiblePresentation,
                    true,
                    runtimeInstanceId,
                    windowNamespace,
                    windowName,
                    window.IsOpen,
                    window.IsFocused,
                    window.Collapsed,
                    window.Position?.X,
                    window.Position?.Y,
                    window.Size?.X,
                    window.Size?.Y));
            }
            catch
            {
                // Window unload/races remain local to this observation.
            }
            ordinal++;
        }
        return surfaces.OrderBy(surface => surface.Label, StringComparer.OrdinalIgnoreCase).ThenBy(surface => surface.Id, StringComparer.Ordinal).ToArray();
    }

    public bool TryResolveWindow(
        object pluginInstance,
        string pluginInternalName,
        string surfaceId,
        out IWindow? window)
    {
        ArgumentNullException.ThrowIfNull(pluginInstance);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginInternalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        var ordinal = 0;
        foreach (var candidate in EnumerateWindows(pluginInstance, DefaultMaximumFields, DefaultMaximumWindows))
        {
            try
            {
                if (string.Equals(
                    StableId(pluginInternalName, candidate.Window.Namespace ?? candidate.SystemNamespace, candidate.Window.WindowName, ordinal),
                    surfaceId,
                    StringComparison.Ordinal))
                {
                    window = candidate.Window;
                    return true;
                }
            }
            catch { }
            ordinal++;
        }
        window = null;
        return false;
    }

    private static IReadOnlyList<WindowCandidate> EnumerateWindows(object pluginInstance, int maximumFields, int maximumWindows)
    {
        var systems = new List<IWindowSystem>();
        var directWindows = new List<IWindow>();
        var pluginAssembly = pluginInstance.GetType().Assembly;
        var pending = new Queue<(object Value, int Depth)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        pending.Enqueue((pluginInstance, 0));
        var inspectedFields = 0;
        while (pending.Count > 0 && visited.Count < DefaultMaximumObjects && inspectedFields < maximumFields)
        {
            var (value, depth) = pending.Dequeue();
            if (!visited.Add(value))
                continue;
            if (value is IWindowSystem direct && !systems.Contains(direct, ReferenceEqualityComparer.Instance))
                systems.Add(direct);
            if (value is IWindow directWindow && !directWindows.Contains(directWindow, ReferenceEqualityComparer.Instance))
                directWindows.Add(directWindow);
            foreach (var field in EnumerateInstanceFields(value.GetType()))
            {
                if (++inspectedFields > maximumFields)
                    break;
                object? fieldValue;
                try { fieldValue = field.GetValue(value); }
                catch (Exception exception) when (exception is FieldAccessException or TargetException) { continue; }
                if (fieldValue is IWindowSystem system)
                {
                    if (!systems.Contains(system, ReferenceEqualityComparer.Instance))
                        systems.Add(system);
                    continue;
                }
                if (fieldValue is IWindow window)
                {
                    if (!directWindows.Contains(window, ReferenceEqualityComparer.Instance))
                        directWindows.Add(window);
                    continue;
                }
                if (fieldValue is null || depth >= DefaultMaximumDepth ||
                    fieldValue.GetType().Assembly != pluginAssembly ||
                    fieldValue is string or Delegate ||
                    fieldValue.GetType().IsValueType)
                    continue;
                pending.Enqueue((fieldValue, depth + 1));
            }
        }
        var result = new List<WindowCandidate>();
        foreach (var system in systems)
        {
            IReadOnlyList<IWindow> windows;
            try { windows = system.Windows; }
            catch { continue; }
            foreach (var window in windows)
            {
                if (result.All(candidate => !ReferenceEquals(candidate.Window, window)))
                    result.Add(new WindowCandidate(window, system.Namespace));
                if (result.Count >= maximumWindows)
                    return result;
            }
        }
        foreach (var window in directWindows)
        {
            if (result.All(candidate => !ReferenceEquals(candidate.Window, window)))
                result.Add(new WindowCandidate(window, null));
            if (result.Count >= maximumWindows)
                break;
        }
        return result;
    }

    private static IEnumerable<FieldInfo> EnumerateInstanceFields(Type type)
    {
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return field;
    }

    private static string StripImGuiId(string value)
    {
        var separator = value.IndexOf("###", StringComparison.Ordinal);
        return separator < 0 ? value : value[..separator];
    }

    private static string StableId(string pluginInternalName, string? windowNamespace, string windowName, int ordinal)
    {
        var key = $"{pluginInternalName}\n{windowNamespace}\n{windowName}\n{ordinal}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..16];
        return $"plugin.{pluginInternalName}.window.{hash}";
    }

    private readonly record struct WindowCandidate(IWindow Window, string? SystemNamespace);
}
