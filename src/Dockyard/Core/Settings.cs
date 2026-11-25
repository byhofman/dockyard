using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dockyard.Core;

sealed class Settings
{
    public string? Distro { get; set; }
    public bool StopOtherOnSwitch { get; set; } = true;
    public bool StartActiveAtLogon { get; set; } = true;
    // Components removed on purpose, so that rerunning the wizard or updating does not bring them back.
    public List<string> RemovedComponents { get; set; } = [];
    public string? RegistryMirrors { get; set; }
    public string? InsecureRegistries { get; set; }
    public string? AddressPools { get; set; }
    public bool HttpsEnabled { get; set; }
    // Tools from winget, Homebrew or apt/dnf where there is one; otherwise Dockyard's own pinned copies.
    public bool UsePackageManager { get; set; } = true;
    // k, completion, $do and $now in the shell's profile.
    public bool KubectlShell { get; set; }

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

    // Writes only what changed since this copy was loaded, over what is on disk now: the Toolbox, a setup run and the
    // wizard each hold a copy, and a whole stale copy would undo what another one saved meanwhile.
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
        var current = Load(file);
        Distro = current.Distro;
        StopOtherOnSwitch = current.StopOtherOnSwitch;
        StartActiveAtLogon = current.StartActiveAtLogon;
        RemovedComponents = current.RemovedComponents;
        RegistryMirrors = current.RegistryMirrors;
        InsecureRegistries = current.InsecureRegistries;
        AddressPools = current.AddressPools;
        HttpsEnabled = current.HttpsEnabled;
        UsePackageManager = current.UsePackageManager;
        KubectlShell = current.KubectlShell;
        loaded = current.loaded;
    }

    JsonObject AsJson() => JsonSerializer.SerializeToNode(this, SettingsJson.Default.Settings)!.AsObject();
}
