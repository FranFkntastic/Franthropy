using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Miosuke.Messages;

namespace Miosuke.Configuration;

public static class MioConfig
{
    public static string? ConfigDirectoryOverride { get; set; } = null;
    public static string ConfigDirectory
    {
        get => ConfigDirectoryOverride == null ?
            Service.PluginInterface.GetPluginConfigDirectory() :
            Path.Combine(new DirectoryInfo(Service.PluginInterface.GetPluginConfigDirectory()).Parent!.FullName, ConfigDirectoryOverride);
    }
    public static string MainConfigFileName { get; set; } = "main.json";
    public static string MainConfigFile => Path.Combine(ConfigDirectory, MainConfigFileName);
    public static event System.Action? OnSave;
    public static readonly ConfigIo configIo = new();
    public static IMioConfig? Config { get; private set; }



    public static void Setup(string? ConfigDirectoryOverride = null, string? MainConfigFileName = null)
    {
        if (ConfigDirectoryOverride != null) MioConfig.ConfigDirectoryOverride = ConfigDirectoryOverride;
        if (MainConfigFileName != null) MioConfig.MainConfigFileName = MainConfigFileName;
    }

    public static T Init<T>() where T : IMioConfig, new()
    {
        // pre-init
        Directory.CreateDirectory(ConfigDirectory);
        // init
        Config = LoadConfiguration<T>(MainConfigFile);
        return (T)Config;
    }

    public static void Save()
    {
        if (Config != null) Save(Config, MainConfigFile, false);
    }

    public static void Save(this IMioConfig config, string? path = null, bool isRelativePath = true, bool async = true)
    {
        path ??= MainConfigFileName;
        if (isRelativePath) path = Path.Combine(ConfigDirectory, path);

        OnSave?.Invoke();
        var jsonUtf8Bytes = configIo.Serialize(config);

        void Write()
        {
            try
            {
                lock (config)
                {
                    var tempConfig = $"{path}.new";
                    if (File.Exists(tempConfig))
                    {
                        var saveTo = $"{tempConfig}.{DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
                        Service.Log.Warning($"Detected unsuccessfully saved file {tempConfig}: moving to {saveTo}");
                        Notice.Warning("Detected unsuccessfully saved configuration file.");
                        File.Move(tempConfig, saveTo);
                        Service.Log.Warning($"Success. Please manually check {saveTo} file contents.");
                    }
                    File.WriteAllBytes(tempConfig, jsonUtf8Bytes);
                    File.Move(tempConfig, path, true);
                }
            }
            catch (Exception e)
            {
                Service.Log.Error(e, "Failed to save configuration");
            }
        }

        if (async)
            Task.Run(Write);
        else
            Write();
    }

    private static T LoadConfiguration<T>(string path, bool isPathRelative = true) where T : IMioConfig, new()
    {
        if (isPathRelative) path = Path.Combine(ConfigDirectory, path);
        if (!File.Exists(path)) return new T();

        // migration: if is utf8 with BOM, remove BOM
        CheckAndRemoveUtf8Bom(path);

        try
        {
            // this should never return null, but just in case
            return configIo.Deserialize<T>(File.ReadAllBytes(path)) ?? new T();
        }
        catch (Exception e)
        {
            Service.Log.Error(e, $"Config file {path} is corrupted or has invalid format. Loading default configuration.");
            Notice.Error($"Config file is corrupted or has invalid format. Loading default configuration.");
            return new T();
        }
    }

    /// <summary>
    /// Migrates default configuration to MioConfig. Must be called before Init. Get it from: <br />
    /// Service.PluginInterface.ConfigFile <br />
    /// Path.Combine(new DirectoryInfo(Service.PluginInterface.GetPluginConfigDirectory()).Parent!.FullName, "file_name.json")
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <exception cref="NullReferenceException"></exception>
    public static void Migrate<T>(string oldConfigPath) where T : IMioConfig, new()
    {
        if (Config != null)
        {
            throw new NullReferenceException("Migrate must be called before initialization");
        }
        var path = MainConfigFile;
        var oldConfig = new FileInfo(oldConfigPath);

        // move single file config to a config folder
        if (!File.Exists(path) && oldConfig.Exists)
        {
            Service.Log.Warning($"Migrating {oldConfig} into EzConfig system");
            Config = LoadConfiguration<T>(oldConfig.FullName, false);
            Save();
            Config = null;
            File.Move(oldConfig.FullName, $"{oldConfig.FullName}.old");
        }
        else
        {
            Service.Log.Information($"Migrating conditions are not met, skipping...");
        }

        // api 13: migrate int[] to vectors
    }

    private static void CheckAndRemoveUtf8Bom(string path)
    {
        var bom = Encoding.UTF8.GetPreamble();
        var fileBytes = File.ReadAllBytes(path);
        if (fileBytes.Length >= bom.Length)
        {
            var hasBom = true;
            for (var i = 0; i < bom.Length; i++)
            {
                if (fileBytes[i] != bom[i])
                {
                    hasBom = false;
                    break;
                }
            }
            if (hasBom)
            {
                var newBytes = new byte[fileBytes.Length - bom.Length];
                Array.Copy(fileBytes, bom.Length, newBytes, 0, newBytes.Length);
                File.WriteAllBytes(path, newBytes);
                Service.Log.Info($"Removed UTF-8 BOM from config file {path}");
            }
        }
    }

    internal static void Dispose()
    {
        OnSave = null;
    }

}



public interface IMioConfig
{
}



public class ConfigIo
{
    private readonly JsonSerializerOptions deserializeOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Replace,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };
    private readonly JsonSerializerOptions serializeOptions = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public virtual T? Deserialize<T>(ReadOnlySpan<byte> utf8Json) => JsonSerializer.Deserialize<T>(utf8Json, deserializeOptions);
    public virtual byte[] Serialize(object config) => JsonSerializer.SerializeToUtf8Bytes(config, serializeOptions);
}
