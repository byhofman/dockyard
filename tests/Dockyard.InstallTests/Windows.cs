using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Dockyard.InstallTests;

public sealed class WindowsInstalled() : InstallFixture(InstallOs.Windows)
{
    static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string AppDir = Path.Combine(ProgramFiles, "Dockyard");
    public static readonly string Settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockyard", "settings.json");
    public static readonly string DockerConfig = Path.Combine(Profile, ".docker", "config.json");
    public static readonly string WslConfig = Path.Combine(Profile, ".wslconfig");
    public string InstalledCli => Path.Combine(AppDir, "bin", "dockyard.exe");
    public string Tray => Path.Combine(AppDir, "dockyard-tray.exe");
    public string Dashboard => Path.Combine(AppDir, "dockyard-dashboard.exe");

    // winget's copy where it brought one, otherwise Dockyard's own in Program Files; looked up again after every setup.
    string Tool(string name)
    {
        var links = new[]
        {
            Path.Combine(ProgramFiles, "WinGet", "Links", name + ".exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", name + ".exe"),
        };
        return links.FirstOrDefault(File.Exists) ?? Path.Combine(ProgramFiles, "Docker", name + ".exe");
    }

    public string Docker => Tool("docker");
    public string Kind => Tool("kind");
    public string Kubectl => Tool("kubectl");

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        if (!Active) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        await File.WriteAllTextAsync(Settings, """{"StopOtherOnSwitch":false,"StartActiveAtLogon":false}""");
        await SetupOk("--without", "dashboard", "--no-start");
    }

    public Task<Result> Linux(params string[] args) => Sh.Run(Docker, ["--context", "linux", .. args]);

    public async Task<Result> LinuxOk(params string[] args)
    {
        var result = await Linux(args);
        Assert.True(result.Exit == 0, $"docker {string.Join(' ', args)}: {result}");
        return result;
    }

    public static async Task<string> Ps(string script) => (await Sh.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])).Output;

    public async Task StartLinuxEngine()
    {
        Process.Start(new ProcessStartInfo(InstalledCli, "engine start linux") { UseShellExecute = false, CreateNoWindow = true });
        await Sh.Until(async () =>
        {
            try { return (await Sh.Run(Docker, ["--context", "linux", "version"], null, null, 30)).Exit == 0; }
            catch (TimeoutException) { return false; }
        }, "the Linux engine to answer");
    }

    // What a container printed, from its logs and not through an attached docker run.
    public async Task<string> InContainer(string command)
    {
        var id = (await LinuxOk("run", "-d", "alpine:3.24", "sh", "-c", command)).Output;
        await Linux("wait", id);
        var output = (await Linux("logs", id)).Output;
        await Linux("rm", "-f", id);
        return output;
    }

    // The tray is an AOT program that loads Skia's native libraries next to it: a missing one ends it within seconds of its start.
    public async Task<bool> TrayStaysUp(int seconds = 10)
    {
        await StopTray();
        using var started = Process.Start(Tray)!;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var up = !started.HasExited;
        await StopTray();
        return up;
    }

    public static Task<string> StopTray() => Ps("Get-Process dockyard-tray -ErrorAction SilentlyContinue | Stop-Process -Force");

    public static async Task<string> Distros() => (await Sh.Run("wsl.exe", ["-l", "-q"])).Output;

    public static X509Certificate2? TrustedCa()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.FirstOrDefault(c => c.Subject.Contains("Dockyard Local CA"));
    }

    public static async Task<bool> NamesRouted() =>
        (await Ps("@(Get-DnsClientNrptRule | Where-Object { $_.Namespace -contains '.dockyard.internal' }).Count")).Trim() != "0";
}

public sealed class WindowsInstallTests(WindowsInstalled windows) : IClassFixture<WindowsInstalled>
{
    [InstallFact(InstallOs.Windows)]
    public async Task Wrong_arguments_exit_2()
    {
        Assert.Equal(2, (await windows.Setup("--typo")).Exit);
    }

    [InstallFact(InstallOs.Windows)]
    public async Task Installs_without_the_dashboard_and_a_rerun_keeps_the_choices()
    {
        Assert.True(File.Exists(windows.InstalledCli));
        Assert.True(File.Exists(windows.Tray));
        Assert.False(File.Exists(windows.Dashboard));
        Assert.True(await windows.TrayStaysUp(), "the tray runs without the dashboard, on libraries of its own");
        Assert.Contains("dockyard", await WindowsInstalled.Distros());

        await windows.SetupOk("--no-start");
        Assert.False(File.Exists(windows.Dashboard));
        Assert.Matches(@"""StopOtherOnSwitch"":\s*false", await File.ReadAllTextAsync(WindowsInstalled.Settings));
        Assert.Contains("linux", (await windows.Ok(windows.Docker, "context", "ls", "-q")).Output);
    }
}

public sealed class WindowsComponentTests(WindowsInstalled windows) : IClassFixture<WindowsInstalled>
{
    [InstallFact(InstallOs.Windows)]
    public async Task The_dashboard_is_added_and_removed_and_a_running_one_is_stopped()
    {
        await windows.Ok(windows.InstalledCli, "component", "add", "dashboard");
        Assert.True(File.Exists(windows.Dashboard));
        Assert.True(File.Exists(Path.Combine(WindowsInstalled.AppDir, "dashboard.files")), "with its libraries");
        Assert.Equal("Dockyard Dashboard", FileVersionInfo.GetVersionInfo(windows.Dashboard).FileDescription);

        using var running = Process.Start(windows.Dashboard)!;
        await Task.Delay(5000);
        Assert.Equal("dockyard-dashboard", running.ProcessName);
        await windows.Ok(windows.InstalledCli, "component", "remove", "dashboard");
        Assert.True(running.WaitForExit(30000), "removing it stops it");
        Assert.False(File.Exists(windows.Dashboard));
        Assert.True(File.Exists(windows.Tray));
        Assert.True(await windows.TrayStaysUp(), "the tray still runs once the dashboard and its libraries are gone");
    }

    [InstallFact(InstallOs.Windows)]
    public async Task A_component_goes_and_comes_back_and_the_last_engine_refuses()
    {
        await windows.Ok(windows.InstalledCli, "component", "remove", "kind");
        Assert.False(File.Exists(windows.Kind), "kind is gone");
        await windows.SetupOk("--no-start");
        Assert.False(File.Exists(windows.Kind), "it stays gone on a rerun");
        await windows.Ok(windows.InstalledCli, "component", "add", "kind");
        Assert.Matches(@"^kind v", (await windows.Ok(windows.Kind, "version")).Output);
        await windows.Ok(windows.Kubectl, "version", "--client");

        await windows.Ok(windows.InstalledCli, "component", "remove", "compose");
        Assert.NotEqual(0, (await Sh.Run(windows.Docker, ["compose", "version"])).Exit);
        await windows.Ok(windows.InstalledCli, "component", "add", "compose");
        await windows.Ok(windows.Docker, "compose", "version");

        Assert.Equal(2, (await windows.Setup("--no-start", "--without", "windows-engine,linux-engine")).Exit);
    }
}

public sealed class WindowsEngineTests(WindowsInstalled windows) : IClassFixture<WindowsInstalled>
{
    [InstallFact(InstallOs.Windows)]
    public async Task The_engine_runs_containers_and_keeps_a_volume_over_a_rerun()
    {
        await windows.StartLinuxEngine();
        await windows.LinuxOk("volume", "create", "dockyard-e2e");

        await windows.LinuxOk("run", "-d", "--name", "e2e-exec", "alpine:3.24", "sh", "-c", "echo exec-works > /marker; sleep 3600");
        try
        {
            Assert.Equal("exec-works", (await windows.LinuxOk("exec", "e2e-exec", "cat", "/marker")).Output);
            Assert.Equal("attached", (await windows.LinuxOk("run", "--rm", "alpine:3.24", "echo", "attached")).Output);
            var piped = await Sh.Run(windows.Docker, ["--context", "linux", "exec", "-i", "e2e-exec", "cat"], null, "through-stdin");
            Assert.Equal("through-stdin", piped.Output);
        }
        finally { await windows.Linux("rm", "-f", "e2e-exec"); }

        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            await Assert.ThrowsAnyAsync<Exception>(() => http.GetStringAsync("http://127.0.0.1:2375/_ping"));

        await windows.SetupOk("--no-start");
        await windows.StartLinuxEngine();
        await windows.LinuxOk("volume", "inspect", "dockyard-e2e");
    }

    [InstallFact(InstallOs.Windows)]
    public async Task host_docker_internal_reaches_a_service_on_Windows()
    {
        await windows.StartLinuxEngine();
        // WSL with NAT lets only the gateway past Windows' firewall; a CI runner has no mirrored networking and no one to
        // ask, so only there is the firewall turned off, for this test.
        var runner = Gate.Flag("GITHUB_ACTIONS");
        if (runner) await WindowsInstalled.Ps("Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled False");
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://+:18080/");
        try
        {
            listener.Start();
            var serving = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync();
                await context.Response.OutputStream.WriteAsync("from-windows"u8.ToArray());
                context.Response.Close();
            });
            Assert.Equal("from-windows", await windows.InContainer("wget -qO- -T 20 http://host.docker.internal:18080/ 2>&1"));
            await serving;
        }
        finally
        {
            if (listener.IsListening) listener.Stop();
            if (runner) await WindowsInstalled.Ps("Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled True");
        }
        Assert.Equal("refused", await windows.InContainer("wget -qO- -T 5 http://host.docker.internal:2375/version 2>/dev/null || echo refused"));
        Assert.Contains("Address", await windows.InContainer("nslookup dl-cdn.alpinelinux.org 2>&1"));
    }

    [InstallFact(InstallOs.Windows)]
    public async Task Container_domains_resolve_and_https_is_trusted_after_trust_https()
    {
        await windows.StartLinuxEngine();
        Assert.True(await WindowsInstalled.NamesRouted(), "windows sends the name space to Dockyard");
        await windows.LinuxOk("run", "-d", "--name", "e2e-web", "nginx:alpine");
        try
        {
            using var tray = Process.Start(windows.Tray)!;
            await Sh.Until(async () => (await WindowsInstalled.Ps("(Resolve-DnsName e2e-web.dockyard.internal -Type A -ErrorAction SilentlyContinue).IPAddress")).Contains("127.77.0.1"), "the name to resolve", 60);
            Assert.Equal("200", (await windows.Ok("curl.exe", "-s", "-o", "NUL", "-w", "%{http_code}", "--max-time", "15", "http://e2e-web.dockyard.internal/")).Output);
            await WindowsInstalled.StopTray();

            await windows.SetupOk("--no-start", "--trust-https");
            var ca = WindowsInstalled.TrustedCa();
            Assert.NotNull(ca);
            Assert.Contains(ca.Extensions, e => e.Oid?.Value == "2.5.29.30" && e.Critical);
            using var again = Process.Start(windows.Tray)!;
            await Task.Delay(15000);
            // --ssl-no-revoke: this curl asks Windows to check revocation and fails when a private CA publishes no such list.
            Assert.Equal("200", (await windows.Ok("curl.exe", "-s", "--ssl-no-revoke", "-o", "NUL", "-w", "%{http_code}", "--max-time", "15", "https://e2e-web.dockyard.internal/")).Output);
            await WindowsInstalled.StopTray();

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockyard", "ca");
            Assert.False(File.Exists(Path.Combine(folder, "ca.key")), "no plain key on disk, C: being visible from WSL");
            Assert.DoesNotContain("PRIVATE KEY", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(Path.Combine(folder, "ca.key.dpapi"))));
        }
        finally
        {
            await WindowsInstalled.StopTray();
            await windows.Linux("rm", "-f", "e2e-web");
        }
    }

    [InstallFact(InstallOs.Windows)]
    public async Task A_kind_cluster_is_created_reachable_from_Windows_and_deleted()
    {
        await windows.StartLinuxEngine();
        // As a new terminal has it: the Docker folder on the PATH, and kind following the docker context.
        var env = new Dictionary<string, string?> { ["DOCKER_CONTEXT"] = "linux", ["PATH"] = Path.GetDirectoryName(windows.Docker) + ";" + Environment.GetEnvironmentVariable("PATH") };
        var created = await Sh.Run(windows.Kind, ["create", "cluster", "--name", "e2e", "--wait", "180s"], env);
        try
        {
            Assert.True(created.Exit == 0, created.ToString());
            Assert.Contains("e2e", (await Sh.Run(windows.Kind, ["get", "clusters"], env)).Output);
            var kubeconfig = (await Sh.Run(windows.Kind, ["get", "kubeconfig", "--name", "e2e"], env)).Output;
            var server = Regex.Match(kubeconfig, @"server: (\S+)").Groups[1].Value;
            Assert.StartsWith("https://127.0.0.1:", server);
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { Timeout = TimeSpan.FromSeconds(15) };
            Assert.Equal("ok", await http.GetStringAsync($"{server}/livez"));
        }
        finally { await Sh.Run(windows.Kind, ["delete", "cluster", "--name", "e2e"], env); }
        Assert.Equal("", (await windows.LinuxOk("ps", "-aq", "--filter", "label=io.x-k8s.kind.cluster=e2e")).Output);
    }
}

public sealed class WindowsUninstallTests(WindowsInstalled windows) : IClassFixture<WindowsInstalled>
{
    [InstallFact(InstallOs.Windows)]
    public async Task Uninstall_leaves_nothing_but_the_data_and_a_fresh_install_finds_it()
    {
        await windows.StartLinuxEngine();
        await windows.LinuxOk("volume", "create", "dockyard-e2e");

        await windows.Ok(windows.Cli, "uninstall");
        Assert.False(File.Exists(windows.InstalledCli));
        Assert.False(File.Exists(windows.Tray));
        Assert.Contains("dockyard", await WindowsInstalled.Distros());
        Assert.Null(WindowsInstalled.TrustedCa());
        Assert.False(await WindowsInstalled.NamesRouted(), "names no longer go to Dockyard");
        Assert.False(File.Exists(WindowsInstalled.DockerConfig) && (await File.ReadAllTextAsync(WindowsInstalled.DockerConfig)).Contains("credsStore"), "no credential store left in the config");
        Assert.False(File.Exists(WindowsInstalled.WslConfig) && (await File.ReadAllTextAsync(WindowsInstalled.WslConfig)).Contains("networkingMode=mirrored"), "our .wslconfig line is gone");

        await windows.SetupOk("--no-start");
        Assert.True(File.Exists(windows.Dashboard), "a fresh install gets the dashboard");
        await windows.StartLinuxEngine();
        await windows.LinuxOk("volume", "inspect", "dockyard-e2e");
    }
}
