using System.Runtime.Versioning;
using Dockyard.Core;
using Dockyard.Engine;

namespace Dockyard.Hosts;

// Installing and uninstalling on a Linux host: the app, the agent and daemon.json through setup-linux.sh, the tray's
// autostart, the credential store and kind.
[SupportedOSPlatform("linux")]
sealed class LinuxSetup(LinuxRoot root, LinuxComponents components)
{
    const string DaemonJson = "/etc/docker/daemon.json";

    public static string AutostartFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(Paths.UserProfile, ".config"),
        "autostart", "dockyard.desktop");

    static string SetupUser => Environment.UserName == "root" ? Environment.GetEnvironmentVariable("SUDO_USER") ?? "" : Environment.UserName;

    public async Task Install(Action<string> log, InstallRequest request, IEngineHost host)
    {
        if (LinuxComponents.HasDashboard && !request.Dashboard) await components.StopDashboard();
        await components.InstallApp(log, request.Dashboard, tag: null);
        await InstallWithAgent(log);
        if (Environment.UserName == "root")
            log("Running as root: the tray autostart is set up the first time you run setup as your own user");
        else
        {
            await WriteAutostart();
            log($"{Paths.AppName} autostarts at login ({AutostartFile})");
        }
        await SetUpCredentialStore(log);
        if (request.Without.Contains(Core.Kind.Id))
        {
            if (File.Exists(LinuxLayout.KindMarker)) await components.RemoveKind(log);
        }
        else if (await components.Installed(Core.Kind.Id) != Core.Kind.Version) await components.InstallKind(log);
        if (request.TrustHttps) await Https.Enable(host, log, request.Silent);
        await root.TryRun("gnome-extensions", "enable", "appindicatorsupport@rgcjonas.gmail.com");
        if ((await root.TryRun("docker", "context", "show"))?.Output.Trim() is { Length: > 0 } current && current != "default")
        {
            await root.TryRun("docker", "context", "use", "default");
            log($"The docker context was '{current}'; Dockyard manages 'default', which is selected now");
        }
    }

    public async Task Uninstall(Action<string> log, bool removeData, IEngineHost host)
    {
        if (File.Exists(AutostartFile)) File.Delete(AutostartFile);
        await CredentialStore.Forget(Paths.CredentialStoreMarker, LinuxLayout.CredentialHelper, log);
        await Https.Disable(host, log);
        await UninstallWithConfig(log);
        if (Directory.Exists(LinuxLayout.HomeDir)) Directory.Delete(LinuxLayout.HomeDir, recursive: true);
        if (new FileInfo(LinuxLayout.HomeLink).LinkTarget is not null) File.Delete(LinuxLayout.HomeLink);
        if (Directory.Exists(Paths.SettingsDir)) Directory.Delete(Paths.SettingsDir, recursive: true);
        if (removeData) log("Images and containers belong to the system's Docker and were kept; docker system prune removes them");
    }

    // The setup script runs as root but has no JSON tools, so the merged file is prepared here and handed to it.
    async Task InstallWithAgent(Action<string> log)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var agent = await GuestBundle.ExtractAgent(dir) ?? "";
            var existing = File.Exists(DaemonJson) ? await File.ReadAllTextAsync(DaemonJson) : null;
            var config = Path.Combine(dir, "daemon.json");
            var kept = "";
            // Without systemd nothing runs the agent, and a dns entry naming it would leave containers without any DNS.
            if (agent.Length == 0 || !Directory.Exists("/run/systemd/system")) await File.WriteAllTextAsync(config, existing ?? "{}");
            else
            {
                await File.WriteAllTextAsync(config, EngineConfig.WithDockyard(existing, HostProxySettings.FromEnvironment(Environment.GetEnvironmentVariable)));
                // Once the file names the agent, the servers kept from before it are the ones to stay. An empty file tells
                // setup there are none.
                if (!EngineConfig.NamesTheAgent(existing))
                {
                    kept = Path.Combine(dir, "dns-upstreams");
                    await File.WriteAllTextAsync(kept, EngineConfig.KeptDns(existing) ?? "");
                }
                if (EngineConfig.DnsIsSomeoneElses(existing))
                    log($"{DaemonJson} named its own DNS servers; containers keep using them, through Dockyard's agent");
            }
            // Root reads these through a path a normal user's temp folder may keep from it otherwise.
            File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101);
            await root.RunSetup(log, "install", SetupUser, agent, config, kept);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    async Task UninstallWithConfig(Action<string> log)
    {
        var config = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}.json");
        try
        {
            var kept = File.Exists(AgentFiles.KeptDns) ? await File.ReadAllTextAsync(AgentFiles.KeptDns) : null;
            if (File.Exists(DaemonJson)) await File.WriteAllTextAsync(config, EngineConfig.WithoutDockyard(await File.ReadAllTextAsync(DaemonJson), kept));
            await root.RunSetup(log, "uninstall", "", File.Exists(config) ? config : "");
        }
        finally
        {
            File.Delete(config);
        }
    }

    // A server without a desktop has no secret service to keep logins in. Setting the store anyway would make docker login
    // fail outright, so the helper has to answer first.
    async Task SetUpCredentialStore(Action<string> log)
    {
        if (Environment.UserName == "root" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        {
            log("No desktop session here, so no OS credential store: docker login keeps Docker's default (base64 in ~/.docker/config.json)");
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        try
        {
            if (!File.Exists(LinuxLayout.CredentialHelper))
            {
                Directory.CreateDirectory(dir);
                var download = Path.Combine(dir, "helper");
                await Pinned.Download(Pinned.CredentialHelper("secretservice", "linux-amd64"), download);
                await root.RunElevated(["install", "-m", "0755", download, LinuxLayout.CredentialHelper]);
            }
            if ((await root.TryRun(LinuxLayout.CredentialHelper, "list")) is { ExitCode: 0 })
                await CredentialStore.Adopt(log, "secretservice", LinuxLayout.CredentialHelper, Paths.CredentialStoreMarker);
            else
                log("The desktop has no working secret service, so docker login keeps Docker's default (base64 in ~/.docker/config.json)");
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException)
        {
            log($"The credential store was not set up ({e.Message}); running the wizard again retries it");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    static async Task WriteAutostart()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
        await File.WriteAllTextAsync(AutostartFile, DesktopEntry.Autostart(LinuxLayout.InstalledExe));
    }
}
