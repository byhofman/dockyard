using System.Text.Json;

namespace Dockyard.Dashboard.Shell;

static class EngineSwitch
{
    public static string Name(string engine) => engine == "win" ? "Windows" : "Linux";

    public static string Other(string engine) => engine == "win" ? "linux" : "win";

    // The tray's own setting, in the tray's file next to the dashboard's.
    public static bool StopsOther(string? settingsFile = null)
    {
        var file = settingsFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockyard", "settings.json");
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            return !json.RootElement.TryGetProperty("StopOtherOnSwitch", out var value) || value.ValueKind != JsonValueKind.False;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return true; }
    }

    public static string Message(string engine, bool stopsOther)
    {
        var other = Name(Other(engine));
        return $"The dashboard, the docker CLI and tools that follow the docker context then use the {Name(engine)} engine. "
            + (stopsOther
                ? $"The {other} engine stops, and its running containers with it. That is set under Engines and settings."
                : $"The {other} engine keeps running.");
    }

    // What the tray says on the dashboard's pipe when a switch it was asked for has ended.
    public static (string Engine, string? Error)? ParseResult(string line)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts switch
        {
            ["switched", var e] => (e, null),
            ["switch-failed", var e, var message] => (e, message),
            ["switch-failed", var e] => (e, "the tray gave no reason"),
            _ => null,
        };
    }
}
