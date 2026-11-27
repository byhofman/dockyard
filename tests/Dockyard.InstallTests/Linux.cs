using System.Net;
using System.Text.RegularExpressions;

namespace Dockyard.InstallTests;

public sealed class LinuxInstalled() : InstallFixture(InstallOs.Linux)
{
    public const string AppDir = "/usr/lib/dockyard";
    public const string DashboardDir = "/usr/lib/dockyard-dashboard";
    public const string Marker = "/etc/dockyard/installed";
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string CaFolder = Path.Combine(Home, ".config", "Dockyard", "ca");

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        if (!Active) return;
        // GitHub's runner image comes with a kind of its own in /usr/local/bin, which Dockyard would rightly leave alone.
        if (Gate.Flag("GITHUB_ACTIONS")) await Sudo("rm", "-f", "/usr/local/bin/kind");
        await SetupOk("--with", "tray", "--without", "dashboard", "--no-start");
        // docker works in this session without signing out: the socket is recreated with an ACL for this user.
        await Sudo("systemctl", "stop", "docker.service", "docker.socket");
        await Sudo("systemctl", "start", "docker.socket");
    }

    public static Task<Result> Sudo(params string[] args) => Sh.Run("sudo", args);

    public static async Task<Result> SudoOk(params string[] args)
    {
        var result = await Sudo(args);
        Assert.True(result.Exit == 0, $"sudo {string.Join(' ', args)}: {result}");
        return result;
    }

    public static Task<Result> Docker(params string[] args) => Sh.Run("docker", args);

    public static async Task<Result> DockerOk(params string[] args)
    {
        var result = await Docker(args);
        Assert.True(result.Exit == 0, $"docker {string.Join(' ', args)}: {result}");
        return result;
    }

    public static async Task<string> Http(string url, int seconds = 15, params string[] extra) =>
        (await Sh.Run("curl", ["-s", "-o", "/dev/null", "-w", "%{http_code}", "--max-time", seconds.ToString(), .. extra, url])).Output;
}

public sealed class LinuxInstallTests(LinuxInstalled linux) : IClassFixture<LinuxInstalled>
{
    [InstallFact(InstallOs.Linux)]
    public async Task Wrong_arguments_exit_2()
    {
        Assert.Equal(2, (await linux.Setup("--typo")).Exit);
    }

    [InstallFact(InstallOs.Linux)]
    public async Task Installs_without_the_dashboard_and_a_rerun_keeps_the_choice()
    {
        Assert.True(File.Exists($"{LinuxInstalled.AppDir}/dockyard-tray"));
        Assert.True(File.Exists($"{LinuxInstalled.AppDir}/dockyard-cli"));
        Assert.False(File.Exists($"{LinuxInstalled.DashboardDir}/dockyard-dashboard"));
        Assert.True(File.Exists(LinuxInstalled.Marker), "setup ran");
        Assert.True(File.Exists("/usr/bin/dockyard"), "dockyard is on the PATH");
        var autostart = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "dockyard.desktop");
        Assert.Contains($"Exec=\"{LinuxInstalled.AppDir}/dockyard-tray\"", await File.ReadAllTextAsync(autostart));
        await LinuxInstalled.DockerOk("volume", "create", "dockyard-e2e");

        await linux.SetupOk("--no-start");
        Assert.False(File.Exists($"{LinuxInstalled.DashboardDir}/dockyard-dashboard"), "still no dashboard");
        await LinuxInstalled.DockerOk("volume", "inspect", "dockyard-e2e");
    }
}

public sealed class LinuxComponentTests(LinuxInstalled linux) : IClassFixture<LinuxInstalled>
{
    [InstallFact(InstallOs.Linux)]
    public async Task The_dashboard_is_added_and_removed_and_the_tray_and_its_libraries_stay()
    {
        await linux.Ok("sudo", linux.Cli, "component", "add", "dashboard");
        Assert.True(File.Exists($"{LinuxInstalled.DashboardDir}/dockyard-dashboard"));
        Assert.True(File.Exists($"{LinuxInstalled.DashboardDir}/libSkiaSharp.so"), "with its libraries");
        await linux.SetupOk("--no-start");
        Assert.True(File.Exists($"{LinuxInstalled.DashboardDir}/dockyard-dashboard"), "a rerun keeps it");
        await linux.Ok("sudo", linux.Cli, "component", "remove", "dashboard");
        Assert.False(File.Exists($"{LinuxInstalled.DashboardDir}/dockyard-dashboard"));
        Assert.True(File.Exists($"{LinuxInstalled.AppDir}/dockyard-tray"));
        Assert.True(File.Exists($"{LinuxInstalled.AppDir}/libSkiaSharp.so"), "and the libraries it links");
    }

    [InstallFact(InstallOs.Linux)]
    public async Task A_component_goes_and_comes_back_and_the_engine_is_required()
    {
        // By the package: this runner also has a compose of its own outside any package.
        async Task<bool> ComposeInstalled() => (await Sh.Run("dpkg", "-s", "docker-compose-plugin")).Output.Contains("Status: install ok");
        await linux.Ok("sudo", linux.Cli, "component", "remove", "compose");
        Assert.False(await ComposeInstalled());
        await linux.SetupOk("--no-start");
        Assert.False(await ComposeInstalled(), "it stays gone on a rerun");
        await linux.Ok("sudo", linux.Cli, "component", "add", "compose");
        Assert.True(await ComposeInstalled());
        Assert.Equal(2, (await linux.Setup("--no-start", "--without", "linux-engine")).Exit);
    }

    [InstallFact(InstallOs.Linux)]
    public async Task The_dashboard_installs_on_its_own_without_the_dockyard_command()
    {
        Assert.SkipWhen(linux.Assets is null, "Needs DOCKYARD_ASSETS, where the dashboard's tarball is");
        await linux.Ok("sudo", linux.Cli, "uninstall");
        var unpack = Directory.CreateTempSubdirectory("dashboard-").FullName;
        try
        {
            var tarball = Directory.GetFiles(linux.Assets!, "dockyard-dashboard-*-linux-x64.tar.gz").Single();
            await Sh.Ok("tar", "-xzf", tarball, "-C", unpack);
            await Sh.Ok("sudo", Path.Combine(unpack, "dockyard-dashboard", "dockyard-dashboard"), "--setup", "--silent", "--no-start", "--without", "cli");
            Assert.True(File.Exists($"{LinuxInstalled.DashboardDir}/dockyard-dashboard"));
            Assert.True(File.Exists($"{LinuxInstalled.DashboardDir}/dockyard-tray"), "with its own tray");
            Assert.False(File.Exists("/usr/bin/dockyard"), "no dockyard command");
            Assert.True(File.Exists(LinuxInstalled.Marker), "setup ran");
        }
        finally { Directory.Delete(unpack, true); }
        await linux.SetupOk("--no-start");
    }
}

public sealed class LinuxEngineTests(LinuxInstalled linux) : IClassFixture<LinuxInstalled>
{
    [InstallFact(InstallOs.Linux)]
    public async Task host_docker_internal_reaches_a_service_on_this_machines_loopback()
    {
        await LinuxInstalled.DockerOk("version");
        await Task.Delay(2000);
        Assert.Equal(0, (await LinuxInstalled.Sudo("systemctl", "is-active", "--quiet", "dockyard-host.service")).Exit);
        await LinuxInstalled.SudoOk("iptables", "-S", "DOCKYARD-INPUT");
        Assert.Contains("198.19.250.1", (await Sh.Ok("ip", "addr", "show", "lo")).Output);

        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18081/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch (Exception) { return; }
                await context.Response.OutputStream.WriteAsync("from-the-host"u8.ToArray());
                context.Response.Close();
            }
        });
        try
        {
            foreach (var network in new[] { new string[0], new[] { "--network", "host" } })
            {
                var reply = await LinuxInstalled.DockerOk(["run", "--rm", .. network, "alpine:3.24", "wget", "-qO-", "-T", "15", "http://host.docker.internal:18081/"]);
                Assert.Equal("from-the-host", reply.Output);
            }
        }
        finally { listener.Stop(); await serving; }
        await LinuxInstalled.DockerOk("run", "--rm", "alpine:3.24", "nslookup", "dl-cdn.alpinelinux.org");
    }

    [InstallFact(InstallOs.Linux)]
    public async Task Container_domains_resolve_and_https_is_trusted_after_trust_https()
    {
        await LinuxInstalled.DockerOk("run", "-d", "--name", "e2e-web", "nginx:alpine");
        try
        {
            foreach (var service in new[] { "dockyard-dns", "dockyard-proxy", "dockyard-etcd" })
                await Sh.Until(async () => (await LinuxInstalled.Docker("inspect", "-f", "{{.State.Running}} {{index .Config.Labels \"dockyard.service\"}}", service)).Output == $"true {service["dockyard-".Length..]}", $"{service} to run", 60);
            Assert.Contains("127.77.0.1", (await Sh.Ok("resolvectl", "query", "e2e-web.dockyard.internal")).Output);
            await Sh.Until(async () => await LinuxInstalled.Http("http://e2e-web.dockyard.internal/", 5) == "200", "http to reach the name", 30);
            Assert.DoesNotContain(":80 ", string.Join('\n', (await Sh.Ok("ss", "-ltn")).Output.Split('\n').Where(l => !Regex.IsMatch(l, @"127\.0\.0\.5[34]"))));
            Assert.Equal("000", await LinuxInstalled.Http("https://e2e-web.dockyard.internal/", 5));

            await linux.SetupOk("--no-start", "--trust-https");
            // Setup restarted the agent and the resolver, and the agent looks containers up again when it starts.
            await Sh.Until(async () => await LinuxInstalled.Http("https://e2e-web.dockyard.internal/", 5) == "200", "https to be trusted", 40);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(LinuxInstalled.CaFolder, "ca.key")));
            var text = (await Sh.Ok("openssl", "x509", "-in", Path.Combine(LinuxInstalled.CaFolder, "ca.pem"), "-noout", "-text")).Output;
            Assert.Matches(@"Name Constraints: critical[\s\S]{0,200}DNS:dockyard\.internal", text);
        }
        finally { await LinuxInstalled.Docker("rm", "-f", "e2e-web"); }
    }

    [InstallFact(InstallOs.Linux)]
    public async Task A_kind_cluster_is_created_reachable_and_deleted_and_kind_comes_back()
    {
        Assert.True(File.Exists("/usr/local/bin/kind"));
        Assert.True(File.Exists("/etc/dockyard/kind"), "marked as Dockyard's");
        await Sh.Ok("kubectl", "version", "--client");
        await Sh.Ok("helm", "version", "--short");
        var created = await Sh.Run("kind", "create", "cluster", "--name", "e2e", "--wait", "180s");
        try
        {
            Assert.True(created.Exit == 0, created.ToString());
            Assert.Contains("e2e", (await Sh.Ok("kind", "get", "clusters")).Output);
            var server = Regex.Match((await Sh.Ok("kind", "get", "kubeconfig", "--name", "e2e")).Output, @"server: (\S+)").Groups[1].Value;
            Assert.StartsWith("https://127.0.0.1:", server);
            Assert.Equal("ok", (await Sh.Ok("curl", "-sk", "--max-time", "10", $"{server}/livez")).Output);
        }
        finally { await Sh.Run("kind", "delete", "cluster", "--name", "e2e"); }
        Assert.Equal("", (await LinuxInstalled.DockerOk("ps", "-aq", "--filter", "label=io.x-k8s.kind.cluster=e2e")).Output);

        await linux.SetupOk("--no-start", "--without", "kind");
        Assert.False(File.Exists("/usr/local/bin/kind"), "kind is gone");
        await linux.SetupOk("--no-start");
        Assert.False(File.Exists("/usr/local/bin/kind"), "it stays gone on a rerun");
        await linux.SetupOk("--no-start", "--with", "kind");
        Assert.True(File.Exists("/etc/dockyard/kind"));
    }
}

public sealed class LinuxUninstallTests(LinuxInstalled linux) : IClassFixture<LinuxInstalled>
{
    [InstallFact(InstallOs.Linux)]
    public async Task Uninstall_leaves_nothing_but_docker_and_its_data_and_a_fresh_install_finds_it()
    {
        await LinuxInstalled.DockerOk("volume", "create", "dockyard-e2e");
        await linux.Ok("sudo", linux.Cli, "uninstall");
        Assert.False(File.Exists($"{LinuxInstalled.AppDir}/dockyard-tray"));
        Assert.False(File.Exists($"{LinuxInstalled.AppDir}/dockyard-cli"));
        Assert.False(File.Exists(LinuxInstalled.Marker), "setup undone");
        foreach (var path in new[]
        {
            "/etc/systemd/system/dockyard-host.service", "/usr/local/bin/kind", "/usr/local/share/ca-certificates/dockyard-local-ca.crt",
            Path.Combine(LinuxInstalled.CaFolder, "ca.key"), "/etc/systemd/resolved.conf.d/dockyard.conf",
        })
            Assert.False(File.Exists(path), path);
        Assert.NotEqual(0, (await Sh.Run("sh", "-c", "grep -rl 'Dockyard Local CA' /etc/ssl/certs")).Exit);
        Assert.DoesNotContain("198.19.250.", (await Sh.Ok("ip", "addr", "show", "lo")).Output);
        var daemon = "/etc/docker/daemon.json";
        Assert.False(File.Exists(daemon) && (await File.ReadAllTextAsync(daemon)).Contains("198.19.250.1"), "daemon.json no longer names the agent");
        var rules = (await LinuxInstalled.SudoOk("sh", "-c", "iptables -S; iptables -t nat -S")).Output;
        Assert.DoesNotMatch(@"198\.19\.250\.|127\.77\.0\.1|DOCKYARD-INPUT", rules);

        await LinuxInstalled.Sudo("systemctl", "reset-failed", "docker.socket", "docker.service");
        await LinuxInstalled.SudoOk("systemctl", "restart", "docker.socket", "docker.service");
        await LinuxInstalled.DockerOk("volume", "inspect", "dockyard-e2e");
        await LinuxInstalled.DockerOk("volume", "rm", "dockyard-e2e");

        await linux.SetupOk("--no-start");
        Assert.True(File.Exists($"{LinuxInstalled.DashboardDir}/dockyard-dashboard"), "a fresh install gets the dashboard");
        Assert.Contains("198.19.250.1", await File.ReadAllTextAsync(daemon));
    }
}
