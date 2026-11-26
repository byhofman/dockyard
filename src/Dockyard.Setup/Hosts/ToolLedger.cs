using System.Text.Json;

namespace Dockyard.Hosts;

// The tools Dockyard itself installed through the package manager, so that moving to the Dockyard source takes those
// away again and leaves alone a package the user had before.
sealed class ToolLedger(string file)
{
    HashSet<string> Read()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(file), ToolLedgerJson.Default.HashSetString) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    void Write(HashSet<string> ids)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(ids, ToolLedgerJson.Default.HashSetString));
    }

    public bool Has(string id) => Read().Contains(id);

    public void Add(string id)
    {
        var ids = Read();
        if (ids.Add(id)) Write(ids);
    }

    public void Remove(string id)
    {
        var ids = Read();
        if (ids.Remove(id)) Write(ids);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(HashSet<string>))]
partial class ToolLedgerJson : System.Text.Json.Serialization.JsonSerializerContext;
