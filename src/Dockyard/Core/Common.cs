using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dockyard.Core;

static class Paths
{
    public static readonly string DockerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker");
    public static readonly string DockerExe = Path.Combine(DockerDir, "docker.exe");
    public static readonly string CredentialHelper = Path.Combine(DockerDir, "docker-credential-wincred.exe");
    public static readonly string DockerdExe = Path.Combine(DockerDir, "dockerd.exe");
    public static readonly string PluginDir = Path.Combine(DockerDir, "cli-plugins");
    public static readonly string KindExe = Path.Combine(DockerDir, "kind.exe");
    public static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Dockyard");
    public static readonly string AppExe = Path.Combine(AppDir, "Dockyard.exe");
    public static readonly string DashboardExe = Path.Combine(AppDir, DashboardProcess + ".exe");
    public static readonly string LegacyDashboardExe = Path.Combine(AppDir, LegacyDashboardProcess + ".exe");
    public const string DashboardProcess = "Dockyard Dashboard";
    // What installs up to v0.2.1 have, running or not.
    public const string LegacyDashboardProcess = "Dockyard.Dashboard";
    // The asset keeps its name: scripts and trays already out there ask for it.
    public const string DashboardZipName = "Dockyard.Dashboard.zip";
    public static readonly string DaemonConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "docker", "config");
    public static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockyard");
    public static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    public static readonly string LegacyTrayDir = Path.Combine(DockerDir, "tray");
    public static readonly string LegacySettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockerTray");
    public const string LegacyName = "DockerTray";

    public const string WindowsPipeName = "docker_windows";
    public const string WindowsHost = "npipe:////./pipe/" + WindowsPipeName;
    // Where dockyard-agent serves the Linux engine to Windows, to the tray's token only.
    public const int LinuxPort = 2375;
    public const string LinuxPipeName = "dockyard_linux";
    public const string LinuxAgentPipeName = "dockyard_linux_agent";
    public const string LinuxHost = "npipe:////./pipe/" + LinuxPipeName;
    // What the linux context pointed at up to v0.4: an open port every Windows user and web page could reach.
    public const string LegacyLinuxHost = "tcp://127.0.0.1:2375";
    public static readonly string EngineTokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dockyard", "engine.token");
    public const string ServiceName = "docker";
    public const string Repo = "byhofman/dockyard";
    public const string AppName = "Dockyard";

    public static string Self => Environment.ProcessPath!;
    public static bool IsInstalledCopy => string.Equals(Path.GetFullPath(Self), AppExe, StringComparison.OrdinalIgnoreCase);

    public static string AppVersion =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static readonly string CredentialStoreMarker = Path.Combine(SettingsDir, "credential-store");

    public static readonly string WslConfigFile = Path.Combine(UserProfile, ".wslconfig");
}

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

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
partial class SettingsJson : JsonSerializerContext;

sealed record ShellResult(int ExitCode, string Output, string Error);

static class Shell
{
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            sb.Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    static ProcessStartInfo Info(string file, IEnumerable<string> args, bool redirect)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
        };
        if (redirect)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        psi.Environment["WSL_UTF8"] = "1";
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static Process Spawn(string file, params string[] args) =>
        Process.Start(Info(file, args, redirect: false)) ?? throw new InvalidOperationException($"Could not start {file}");

    public static async Task<ShellResult> Run(string file, params string[] args)
    {
        using var p = Process.Start(Info(file, args, redirect: true)) ?? throw new InvalidOperationException($"Could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return new ShellResult(p.ExitCode, await stdout, await stderr);
    }

    public static async Task<ShellResult> RunWithInput(string file, string input, params string[] args)
    {
        var info = Info(file, args, redirect: true);
        info.RedirectStandardInput = true;
        using var p = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.StandardInput.WriteAsync(input);
        p.StandardInput.Close();
        await p.WaitForExitAsync();
        return new ShellResult(p.ExitCode, await stdout, await stderr);
    }

    public static async Task<string> Check(string file, params string[] args)
    {
        var r = await Run(file, args);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} {string.Join(' ', args)} failed ({r.ExitCode}): {(r.Error + r.Output).Trim()}");
        return r.Output;
    }

    // An elevated process would start the tray elevated too; explorer.exe launches it as the signed-in user.
    public static void StartUnelevated(string exe) =>
        Process.Start(new ProcessStartInfo("explorer.exe", Quote(exe)) { UseShellExecute = false });
}
