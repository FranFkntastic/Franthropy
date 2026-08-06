using System.Text;
using Franthropy.Dalamud.Persistence;

namespace Franthropy.Dalamud.Tests.Persistence;

public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly string root;

    public JsonConfigStoreTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"franthropy-config-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public sealed class SampleConfig
    {
        public int Version = 1;
        public bool Enabled = true;
        public int[] Hotkey = [9];
        public string SelectedWorld = "";
    }

    private JsonConfigStore<SampleConfig> CreateStore(Action<string, Exception?>? diagnostic = null) =>
        new(new JsonConfigStoreOptions
        {
            ConfigDirectory = root,
            MainConfigFileName = "main.json",
            Diagnostic = diagnostic,
        });

    [Fact]
    public void Load_ReturnsDefaults_WhenFileMissing()
    {
        var store = CreateStore();

        var config = store.Load();

        Assert.Equal(1, config.Version);
        Assert.True(config.Enabled);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsPublicFields()
    {
        var store = CreateStore();
        var config = new SampleConfig
        {
            Version = 7,
            Enabled = false,
            Hotkey = [17, 88],
            SelectedWorld = "Siren",
        };

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal(7, loaded.Version);
        Assert.False(loaded.Enabled);
        Assert.Equal([17, 88], loaded.Hotkey);
        Assert.Equal("Siren", loaded.SelectedWorld);
    }

    [Fact]
    public void Save_SerializesFieldsNotJustProperties()
    {
        var store = CreateStore();

        store.Save(new SampleConfig { Version = 42 });

        var json = File.ReadAllText(store.MainConfigFile);
        Assert.Contains("\"Version\": 42", json);
    }

    [Fact]
    public void Save_LeavesNoTemporaryResidue()
    {
        var store = CreateStore();

        store.Save(new SampleConfig());

        Assert.True(File.Exists(store.MainConfigFile));
        Assert.Equal(
            new[] { store.MainConfigFile },
            Directory.EnumerateFiles(root).Select(Path.GetFullPath));
    }

    [Fact]
    public void Load_ReturnsDefaultsAndReports_WhenFileCorrupted()
    {
        var store = CreateStore();
        File.WriteAllText(store.MainConfigFile, "{ this is not json");
        var diagnostics = new List<string>();
        var observed = CreateStore((message, _) => diagnostics.Add(message));

        var config = observed.Load();

        Assert.Equal(1, config.Version);
        Assert.Single(diagnostics);
        Assert.Contains("corrupted", diagnostics[0]);
    }

    [Fact]
    public void Load_StripsUtf8Bom_InPlace()
    {
        var store = CreateStore();
        store.Save(new SampleConfig { Version = 3 });
        var withoutBom = File.ReadAllBytes(store.MainConfigFile);
        File.WriteAllBytes(store.MainConfigFile, Encoding.UTF8.GetPreamble().Concat(withoutBom).ToArray());

        var loaded = store.Load();

        Assert.Equal(3, loaded.Version);
        Assert.Equal(withoutBom, File.ReadAllBytes(store.MainConfigFile));
    }

    [Fact]
    public void Load_SkipsUnknownMembers_AndIgnoresCase()
    {
        var store = CreateStore();
        File.WriteAllText(store.MainConfigFile, "{ \"version\": 9, \"RemovedSetting\": true }");

        var loaded = store.Load();

        Assert.Equal(9, loaded.Version);
    }

    [Fact]
    public void TryMigrateFrom_MovesLegacyFile_OnlyWhenMainMissing()
    {
        var legacyPath = Path.Combine(root, "ComplicatedMarketBoard.json");
        File.WriteAllText(legacyPath, "{ \"Version\": 5, \"SelectedWorld\": \"Jenova\" }");
        var store = CreateStore();

        var migrated = store.TryMigrateFrom(legacyPath);

        Assert.True(migrated);
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists($"{legacyPath}.old"));
        var loaded = store.Load();
        Assert.Equal(5, loaded.Version);
        Assert.Equal("Jenova", loaded.SelectedWorld);
    }

    [Fact]
    public void TryMigrateFrom_DoesNothing_WhenMainExists()
    {
        var store = CreateStore();
        store.Save(new SampleConfig { Version = 2 });
        var legacyPath = Path.Combine(root, "legacy.json");
        File.WriteAllText(legacyPath, "{ \"Version\": 99 }");

        var migrated = store.TryMigrateFrom(legacyPath);

        Assert.False(migrated);
        Assert.True(File.Exists(legacyPath));
        Assert.Equal(2, store.Load().Version);
    }
}
