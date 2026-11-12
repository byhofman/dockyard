using System.Text.Json;

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

    string file = Paths.SettingsFile;

    public static Settings Load() => Load(Paths.SettingsFile);

    public static Settings Load(string file)
    {
        Settings settings;
        try { settings = JsonSerializer.Deserialize(File.ReadAllText(file), SettingsJson.Default.Settings) ?? new(); }
        catch { settings = new(); }
        settings.file = file;
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, SettingsJson.Default.Settings));
    }
}
