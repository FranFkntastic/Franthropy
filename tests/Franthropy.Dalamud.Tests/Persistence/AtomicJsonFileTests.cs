using System.Text.Json;
using Franthropy.Dalamud.Persistence;

namespace Franthropy.Dalamud.Tests.Persistence;

public sealed class AtomicJsonFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"franthropy-atomic-json-{Guid.NewGuid():N}");

    [Fact]
    public void Write_CreatesParentDirectoryAndRoundTripsDocument()
    {
        var path = Path.Combine(directory, "nested", "state.json");

        AtomicJsonFile.Write(path, new TestDocument("ready", 7));

        Assert.Equal(new TestDocument("ready", 7), AtomicJsonFile.Read<TestDocument>(path));
    }

    [Fact]
    public void Write_ReplacesExistingDocumentWithoutLeavingTemporaryFiles()
    {
        var path = Path.Combine(directory, "state.json");
        AtomicJsonFile.Write(path, new TestDocument("old", 1));

        AtomicJsonFile.Write(path, new TestDocument("new", 2), new JsonSerializerOptions { WriteIndented = true });

        Assert.Equal(new TestDocument("new", 2), AtomicJsonFile.Read<TestDocument>(path));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed record TestDocument(string State, int Revision);
}
