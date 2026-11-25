using System.Net;
using System.Text.Json.Nodes;
using Dockyard.Engine;

namespace Dockyard.Agent;

sealed record ServiceContainer(string Name, string Image, string[] Command, string[] Binds);

// dockyard-dns (CoreDNS), dockyard-proxy (Traefik) and dockyard-etcd, where external-dns in kind clusters publishes names:
// containers on the engine's own network, in the Dockyard group. The agent only writes what they are to serve, from the
// running containers and what the clusters published.
sealed class Services(Func<DockerClient> connect, DomainTable table, string root, Func<IReadOnlyList<DnsView>> currentViews,
    IReadOnlyList<ProxyEntryPoint> entryPoints, Action<string> log)
{
    public const string ServiceLabel = "dockyard.service";
    const string SetupLabel = "dockyard.service.setup";
    public const string DnsImage = "coredns/coredns:1.13.1@sha256:9b9128672209474da07c91439bf15ed704ae05ad918dd6454e5b6ae14e35fee6";
    public const string ProxyImage = "traefik:v3.6@sha256:31267173a15b4944e797a76ffd9c419707c8d8b32fe5b610f80cd0cfa05f372d";
    public const string EtcdImage = "quay.io/coreos/etcd:v3.6.6@sha256:ea8a4698f22200bf0322fd8ed6e0b106a3107db649bd61f654e1a9fc14d9a17e";
    readonly HttpClient etcd = new() { Timeout = TimeSpan.FromSeconds(3) };
    List<ExternalRecord> published = [];
    DateTime publishedRead = DateTime.MinValue;

    string DnsDir => Path.Combine(root, "dns");
    string ProxyDir => Path.Combine(root, "proxy");
    bool tls;
    readonly Dictionary<string, string> zoneRecords = [];
    long serial = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static string ExplainUrl => $"http://{GuestAddresses.Dns}:{GuestAddresses.ExplainPort}";

    static readonly string[] EtcdCommand =
    [
        "/usr/local/bin/etcd", "--name", "dockyard", "--data-dir", "/var/lib/etcd",
        "--listen-client-urls", ExternalRecords.Endpoint, "--advertise-client-urls", ExternalRecords.Endpoint,
        "--listen-peer-urls", $"http://127.0.0.1:{GuestAddresses.EtcdPeerPort}", "--initial-advertise-peer-urls", $"http://127.0.0.1:{GuestAddresses.EtcdPeerPort}",
        "--initial-cluster", $"dockyard=http://127.0.0.1:{GuestAddresses.EtcdPeerPort}",
    ];

    IEnumerable<ServiceContainer> Containers() =>
    [
        new("dockyard-dns", DnsImage, ["-conf", $"{ServiceConfig.ConfigDir}/Corefile"], [$"{DnsDir}:{ServiceConfig.ConfigDir}:ro"]),
        new("dockyard-proxy", ProxyImage, [$"--configFile={ServiceConfig.ConfigDir}/traefik.yml"], [$"{ProxyDir}:{ServiceConfig.ConfigDir}:ro"]),
    ];

    // Only clusters' published names depend on it, so it failing leaves DNS and the proxy as they are.
    static readonly ServiceContainer Etcd = new("dockyard-etcd", EtcdImage, EtcdCommand, ["dockyard-etcd:/var/lib/etcd"]);

    async Task EnsureEtcd(DockerClient docker, CancellationToken ct)
    {
        try { await Ensure(docker, Etcd, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { log($"dockyard-etcd: {e.Message}; names published by clusters are not answered"); }
    }

    public void SetCertificate(string certPem, string keyPem)
    {
        var dir = Path.Combine(ProxyDir, "tls");
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Write(Path.Combine(dir, "cert.pem"), certPem);
        Write(Path.Combine(dir, "key.pem"), keyPem);
        tls = true;
        WriteConfig();
    }

    // Started before the agent's own DNS and proxy would be: when the images cannot be had, offline or blocked, the
    // agent serves names itself as before.
    public async Task<bool> Start(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(ProxyDir, "dynamic"));
            Directory.CreateDirectory(DnsDir);
            tls = File.Exists(Path.Combine(ProxyDir, "tls", "cert.pem"));
            WriteConfig();
            using var docker = connect();
            foreach (var container in Containers()) await Ensure(docker, container, ct);
            await EnsureEtcd(docker, ct);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"the DNS, proxy and etcd containers did not start ({e.Message}); the agent answers for container names itself");
            return false;
        }
    }

    public async Task Run(CancellationToken ct)
    {
        var lastCheck = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            try
            {
                if (DateTime.UtcNow - publishedRead > TimeSpan.FromSeconds(5))
                {
                    publishedRead = DateTime.UtcNow;
                    try { published = await ExternalRecords.Read(etcd, ct); }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
                }
                WriteConfig();
                if (DateTime.UtcNow - lastCheck < TimeSpan.FromSeconds(30)) continue;
                lastCheck = DateTime.UtcNow;
                using var docker = connect();
                foreach (var container in Containers()) await Ensure(docker, container, ct);
                await EnsureEtcd(docker, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log($"services: {e.Message}"); }
        }
    }

    void WriteConfig()
    {
        var views = currentViews();
        var names = table.Names();
        var clusters = table.Clusters();
        // A published name that points at a cluster's ingress goes through the proxy, which every place can reach; any
        // other address is answered as it is.
        ClusterIngress? IngressOf(ExternalRecord r) => clusters.FirstOrDefault(c => c.Cluster == r.Cluster && (r.Address.Equals(c.Http) || r.Address.Equals(c.Https)));
        // A cluster that is stopped or gone has no one to answer for its names.
        var current = published.Where(r => clusters.Any(c => c.Cluster == r.Cluster)).ToList();
        var proxied = current.Where(r => IngressOf(r) is not null).Select(r => (r.Name, IngressOf(r)!)).ToList();
        Write(Path.Combine(DnsDir, "Corefile"), ServiceConfig.Corefile(views));
        foreach (var view in views)
        {
            var zone = Path.Combine(DnsDir, ServiceConfig.ZoneFileName(view));
            var extra = current.Select(r => (r.Name, IngressOf(r) is null ? r.Address : view.Answer)).ToList();
            var records = ServiceConfig.Zone(view.Answer, names, clusters.Select(c => c.Cluster), 0, extra);
            if (records != zoneRecords.GetValueOrDefault(view.Name))
            {
                zoneRecords[view.Name] = records;
                Write(zone, ServiceConfig.Zone(view.Answer, names, clusters.Select(c => c.Cluster), ++serial, extra));
            }
            if (view.Recursive) Write(Path.Combine(DnsDir, $"{view.Name}.special"), ServiceConfig.Hosts(GuestAddresses.Host, GuestAddresses.HostNames));
        }
        Write(Path.Combine(ProxyDir, "traefik.yml"), ServiceConfig.TraefikStatic(entryPoints));
        Write(Path.Combine(ProxyDir, "dynamic", "dockyard.yml"), ServiceConfig.TraefikDynamic(entryPoints, table.Routes(), clusters, ExplainUrl, tls, proxied));
    }

    // Replaced whole, so a reader never sees half a file; left alone when nothing changed, so nothing reloads for nothing.
    static void Write(string file, string content)
    {
        if (File.Exists(file) && File.ReadAllText(file) == content) return;
        var temporary = file + ".new";
        File.WriteAllText(temporary, content);
        File.Move(temporary, file, overwrite: true);
    }

    static string Setup(ServiceContainer c) => string.Join(' ', [c.Image, .. c.Command, .. c.Binds]);

    async Task Ensure(DockerClient docker, ServiceContainer service, CancellationToken ct)
    {
        var current = (await docker.ListContainersAsync(all: true, ct)).FirstOrDefault(c => c.Names?.Contains("/" + service.Name) == true);
        if (current is not null && current.Labels?.GetValueOrDefault(SetupLabel) == Setup(service))
        {
            if (current.State != "running") await docker.StartContainerAsync(current.Id, ct);
            return;
        }
        if (current is not null) await docker.RemoveContainerAsync(current.Id, force: true, ct: ct);
        if (!(await docker.ListImagesAsync(ct)).Any(i => i.RepoDigests?.Any(d => service.Image.EndsWith(d[d.IndexOf('@')..], StringComparison.Ordinal)) == true))
        {
            log($"pulling {service.Image}");
            await docker.PullImageAsync(service.Image, ct: ct);
        }
        var body = new JsonObject
        {
            ["Image"] = service.Image,
            ["Cmd"] = new JsonArray(service.Command.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
            ["Labels"] = new JsonObject { [ServiceLabel] = service.Name["dockyard-".Length..], [SetupLabel] = Setup(service) },
            ["HostConfig"] = new JsonObject
            {
                ["NetworkMode"] = "host",
                ["RestartPolicy"] = new JsonObject { ["Name"] = "unless-stopped" },
                ["Binds"] = new JsonArray(service.Binds.Select(b => (JsonNode)JsonValue.Create(b)).ToArray()),
            },
        };
        var id = (await docker.PostNodeAsync($"containers/create?name={Uri.EscapeDataString(service.Name)}", body, ct))!["Id"]!.GetValue<string>();
        await docker.StartContainerAsync(id, ct);
        log($"{service.Name} runs");
    }

    public static IReadOnlyList<DnsView> Views(AgentMode mode, IReadOnlyList<IPAddress> containerUpstreams, IPAddress? vmAddress)
    {
        List<DnsView> views = [new("containers", GuestAddresses.Dns, mode == AgentMode.Host ? GuestAddresses.ContainerDnsPort : 53, GuestAddresses.Dns, containerUpstreams, Recursive: true)];
        if (mode is AgentMode.Wsl or AgentMode.Host)
            views.Add(new("host", IPAddress.Loopback, GuestAddresses.RelayDnsPort, GuestAddresses.HostLoopback, [], Recursive: false));
        else if (vmAddress is not null)
            views.Add(new("host", IPAddress.Loopback, 53, vmAddress, [], Recursive: false));
        return views;
    }

    public static IReadOnlyList<ProxyEntryPoint> EntryPoints(AgentMode mode)
    {
        List<ProxyEntryPoint> points =
        [
            new("web", $"{GuestAddresses.Dns}:{GuestAddresses.DomainHttpPort}", false),
            new("websecure", $"{GuestAddresses.Dns}:{GuestAddresses.DomainHttpsPort}", true),
        ];
        if (mode == AgentMode.Wsl)
        {
            points.Add(new("relay", $"127.0.0.1:{GuestAddresses.RelayHttpPort}", false));
            points.Add(new("relaysecure", $"127.0.0.1:{GuestAddresses.RelayHttpsPort}", true));
        }
        if (mode == AgentMode.Host)
        {
            points.Add(new("local", $"127.0.0.1:{GuestAddresses.DomainHttpPort}", false));
            points.Add(new("localsecure", $"127.0.0.1:{GuestAddresses.DomainHttpsPort}", true));
        }
        return points;
    }
}
