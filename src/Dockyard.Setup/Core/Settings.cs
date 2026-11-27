using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dockyard.Core;

sealed class Settings
{
    public bool StopOtherOnSwitch { get; set; } = true;
    public bool StartActiveAtLogon { get; set; } = true;
    // Components removed on purpose, so that rerunning setup or updating does not bring them back.
    public List<string> RemovedComponents { get; set; } = [];
    public bool HttpsEnabled { get; set; }
    // Names under dockyard.internal, the proxy and the names clusters publish; off leaves containers' DNS as it is.
    public bool ContainerDomains { get; set; } = true;
    // Tools from winget, Homebrew or apt/dnf where there is one; otherwise Dockyard's own pinned downloads.
    public ToolSource Source { get; set; } = ToolSource.PackageManager;
    // k, completion, $do and $now in the shell's profile.
    public bool KubectlShell { get; set; }
    // dockctx in the shell's profile while the dockyard command is installed.
    public bool Dockctx { get; set; } = true;

    string file = Paths.SettingsFile;
    JsonObject loaded = [];

    public static Settings Load() => Load(Paths.SettingsFile);

    public static Settings Load(string file)
    {
        Settings settings;
        try { settings = JsonSerializer.Deserialize(File.ReadAllText(file), SettingsJson.Default.Settings) ?? new(); }
        // None yet, or one this version cannot read: the defaults, which the next save writes.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { settings = new(); }
        settings.file = file;
        settings.loaded = settings.AsJson();
        return settings;
    }

    // Writes only what changed since this copy was loaded, over what is on disk now: the dashboard, a setup run and the
    // tray each hold a copy, and a whole stale copy would undo what another one saved meanwhile.
    public void Save()
    {
        var mine = AsJson();
        // Nothing to say when nothing changed: setup runs this every time, also for a user who may not write here.
        if (mine.All(p => JsonNode.DeepEquals(p.Value, loaded[p.Key]))) return;
        var merged = Load(file).AsJson();
        foreach (var (name, value) in mine)
            if (!JsonNode.DeepEquals(value, loaded[name])) merged[name] = value?.DeepClone();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Reload();
    }

    // What is on disk now, which another process may have changed.
    public void Reload()
    {
        var current = Load(file);
        StopOtherOnSwitch = current.StopOtherOnSwitch;
        StartActiveAtLogon = current.StartActiveAtLogon;
        RemovedComponents = current.RemovedComponents;
        HttpsEnabled = current.HttpsEnabled;
        ContainerDomains = current.ContainerDomains;
        Source = current.Source;
        KubectlShell = current.KubectlShell;
        Dockctx = current.Dockctx;
        loaded = current.loaded;
    }

    JsonObject AsJson() => JsonSerializer.SerializeToNode(this, SettingsJson.Default.Settings)!.AsObject();
}
