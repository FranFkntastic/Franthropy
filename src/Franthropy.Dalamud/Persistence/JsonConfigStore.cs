using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Franthropy.Dalamud.Persistence;

/// <summary>
/// Loads and saves one plugin-owned JSON configuration document. The default
/// serializer options are field-based and match the legacy Miosuke MioConfig
/// shape exactly, so existing user configuration files keep their meaning.
/// </summary>
public sealed class JsonConfigStore<T> where T : class, new()
{
    public static JsonSerializerOptions DefaultDeserializeOptions { get; } = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Replace,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static JsonSerializerOptions DefaultSerializeOptions { get; } = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly JsonSerializerOptions deserializeOptions;
    private readonly JsonSerializerOptions serializeOptions;
    private readonly Action<string, Exception?>? diagnostic;

    public string ConfigDirectory { get; }
    public string MainConfigFileName { get; }
    public string MainConfigFile { get; }

    public JsonConfigStore(JsonConfigStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConfigDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MainConfigFileName);

        ConfigDirectory = options.ConfigDirectory;
        MainConfigFileName = options.MainConfigFileName;
        MainConfigFile = Path.Combine(ConfigDirectory, MainConfigFileName);
        deserializeOptions = options.DeserializeOptions ?? DefaultDeserializeOptions;
        serializeOptions = options.SerializeOptions ?? DefaultSerializeOptions;
        diagnostic = options.Diagnostic;
    }

    /// <summary>
    /// Loads the main configuration. A missing file yields defaults; a
    /// corrupted file yields defaults after reporting through the diagnostic
    /// sink. A legacy UTF-8 BOM is stripped in place.
    /// </summary>
    public T Load() => LoadFrom(MainConfigFile);

    /// <summary>
    /// Loads one specific configuration file with the same rules as
    /// <see cref="Load"/>; used for migration sources.
    /// </summary>
    public T LoadFrom(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return new T();
        }

        StripUtf8Bom(path);

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Utf8WithoutBom), deserializeOptions) ?? new T();
        }
        catch (Exception exception)
        {
            diagnostic?.Invoke($"Configuration file '{path}' is corrupted or has an invalid format; loading defaults.", exception);
            return new T();
        }
    }

    /// <summary>
    /// Atomically replaces the main configuration file.
    /// </summary>
    public void Save(T config)
    {
        ArgumentNullException.ThrowIfNull(config);
        AtomicJsonFile.Write(MainConfigFile, config, serializeOptions);
    }

    /// <summary>
    /// Migrates a legacy single-file configuration into the main configuration
    /// path. Runs only when the main file does not exist and the legacy file
    /// does; the legacy file is renamed to '&lt;name&gt;.old' afterwards.
    /// Returns true when a migration happened.
    /// </summary>
    public bool TryMigrateFrom(string legacyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPath);
        if (File.Exists(MainConfigFile) || !File.Exists(legacyPath))
        {
            return false;
        }

        var migrated = LoadFrom(legacyPath);
        Save(migrated);
        File.Move(legacyPath, $"{legacyPath}.old");
        return true;
    }

    private void StripUtf8Bom(string path)
    {
        var preamble = Encoding.UTF8.GetPreamble();
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < preamble.Length)
        {
            return;
        }

        for (var i = 0; i < preamble.Length; i++)
        {
            if (bytes[i] != preamble[i])
            {
                return;
            }
        }

        var stripped = new byte[bytes.Length - preamble.Length];
        Array.Copy(bytes, preamble.Length, stripped, 0, stripped.Length);
        File.WriteAllBytes(path, stripped);
        diagnostic?.Invoke($"Removed UTF-8 BOM from configuration file '{path}'.", null);
    }
}

public sealed class JsonConfigStoreOptions
{
    public required string ConfigDirectory { get; init; }
    public string MainConfigFileName { get; init; } = "main.json";
    public JsonSerializerOptions? DeserializeOptions { get; init; }
    public JsonSerializerOptions? SerializeOptions { get; init; }
    public Action<string, Exception?>? Diagnostic { get; init; }
}
