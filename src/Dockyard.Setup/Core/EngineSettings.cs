using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dockyard.Core;

// The part of an engine's daemon.json that is the user's to edit. Dockyard adds its own keys to it each time it writes the
// file, so editing those has no lasting effect: the editor says so instead of leaving it to be found out.
static class EngineSettings
{
    public const string Linux = "linux";
    public const string Windows = "win";

    // dns points containers at Dockyard's agent, for container domains; proxies are this computer's proxy settings, written
    // only where the file has none of its own; hosts and group make the Windows engine's pipe Dockyard's and the user's.
    public static IReadOnlyList<string> DockyardKeys(string engine) => engine == Windows ? ["hosts"] : ["dns"];

    public static string UserFile(string engine, string? settingsDir = null) =>
        Path.Combine(settingsDir ?? Paths.SettingsDir, "engines", engine + ".daemon.json");

    // An engine whose file Dockyard renders: what the user put in it.
    public static string Load(string engine, string? settingsDir = null)
    {
        var file = UserFile(engine, settingsDir);
        return File.Exists(file) ? File.ReadAllText(file) : "{}";
    }

    public static void Save(string engine, string json, string? settingsDir = null)
    {
        var file = UserFile(engine, settingsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".new";
        File.WriteAllText(temp, json);
        File.Move(temp, file, overwrite: true);
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // Null when the text is a JSON object, otherwise where and why it is not.
    public static string? Problem(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
            return document.RootElement.ValueKind == JsonValueKind.Object ? null : "daemon.json has to be one JSON object, in { }";
        }
        catch (JsonException e)
        {
            return $"Not valid JSON, line {(e.LineNumber ?? 0) + 1}, column {(e.BytePositionInLine ?? 0) + 1}: {FirstSentence(e.Message)}";
        }
    }

    static string FirstSentence(string message) => message.Split(". ")[0].TrimEnd('.');

    // Keys the user set that Dockyard writes over.
    public static IReadOnlyList<string> Overridden(string engine, string json)
    {
        if (Problem(json) is not null) return [];
        var config = JsonNode.Parse(json)!.AsObject();
        return DockyardKeys(engine).Where(config.ContainsKey).ToList();
    }
}
