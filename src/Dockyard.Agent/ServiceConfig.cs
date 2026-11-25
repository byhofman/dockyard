using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Dockyard.Agent;

// One place names are looked up from: its address and port in the engine's Linux, the address container names resolve
// to from there, and whether it also answers everything else from upstream (containers) or only Dockyard's zone (the host).
sealed record DnsView(string Name, IPAddress Bind, int Port, IPAddress Answer, IReadOnlyList<IPAddress> Upstreams, bool Recursive);

sealed record ProxyEntryPoint(string Name, string Address, bool Tls);

sealed record ProxyRoute(string Domain, string Url);

// What dockyard-dns (CoreDNS) and dockyard-proxy (Traefik) are told, written by the agent into folders the containers
// read and reload by themselves.
static class ServiceConfig
{
    public const string ConfigDir = "/etc/dockyard";

    public static string ZoneFileName(DnsView view) => $"{view.Name}.zone";

    public static string Corefile(IEnumerable<DnsView> views)
    {
        var text = new StringBuilder();
        foreach (var view in views)
        {
            text.Append($"{DomainNames.Zone}:{view.Port} {{\n    bind {view.Bind}\n    reload 2s\n    file {ConfigDir}/{ZoneFileName(view)} {{\n        reload 1s\n    }}\n}}\n");
            if (!view.Recursive) continue;
            text.Append($".:{view.Port} {{\n    bind {view.Bind}\n    hosts {ConfigDir}/{view.Name}.special {{\n        ttl 60\n        no_reverse\n        fallthrough\n    }}\n");
            text.Append(view.Upstreams.Count > 0
                ? $"    forward . {string.Join(' ', view.Upstreams.Select(u => u.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{u}]:53" : $"{u}:53"))} {{\n        policy sequential\n    }}\n"
                : "    template ANY ANY {\n        rcode SERVFAIL\n    }\n");
            text.Append("}\n");
        }
        return text.ToString();
    }

    // Authoritative, so a name nobody has is NXDOMAIN, and a cluster's wildcard covers every host under it. The serial
    // is what makes CoreDNS read the file again.
    public static string Zone(IPAddress address, IEnumerable<string> names, IEnumerable<string> clusters, long serial,
        IEnumerable<(string Name, IPAddress Address)>? records = null)
    {
        var origin = DomainNames.Zone;
        var text = new StringBuilder($"$ORIGIN {origin}.\n@ 5 IN SOA ns.{origin}. hostmaster.{origin}. {serial} 60 60 60 5\n@ 5 IN NS ns.{origin}.\nns 5 IN A {address}\n");
        foreach (var name in names.Where(n => n.EndsWith("." + origin, StringComparison.Ordinal)).Select(n => n[..^(origin.Length + 1)]).Distinct().OrderBy(n => n, StringComparer.Ordinal))
            text.Append($"{name} 5 IN A {address}\n");
        foreach (var cluster in clusters.Distinct().OrderBy(c => c, StringComparer.Ordinal))
            text.Append($"*.{cluster} 5 IN A {address}\n");
        var taken = names.ToHashSet();
        foreach (var (name, recordAddress) in (records ?? []).Where(r => !taken.Contains(r.Name) && r.Name.EndsWith("." + origin, StringComparison.Ordinal))
                     .DistinctBy(r => r.Name).OrderBy(r => r.Name, StringComparer.Ordinal))
            text.Append($"{name[..^(origin.Length + 1)]} 5 IN A {recordAddress}\n");
        return text.ToString();
    }

    public static string Hosts(IPAddress address, IEnumerable<string> names)
    {
        var all = names.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        return all.Count == 0 ? "" : $"{address} {string.Join(' ', all)}\n";
    }

    public static string TraefikStatic(IEnumerable<ProxyEntryPoint> entryPoints)
    {
        var text = new StringBuilder("entryPoints:\n");
        foreach (var e in entryPoints) text.Append($"  {e.Name}:\n    address: \"{e.Address}\"\n");
        text.Append($"providers:\n  file:\n    directory: {ConfigDir}/dynamic\n    watch: true\n");
        text.Append("log:\n  level: ERROR\nglobal:\n  checkNewVersion: false\n  sendAnonymousUsage: false\n");
        return text.ToString();
    }

    // Readable, and distinct for names that differ only in punctuation, such as web.shop and web-shop.
    static string Key(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text) builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return $"{builder}-{Convert.ToHexStringLower(hash)[..8]}";
    }

    static JsonArray Names(IEnumerable<ProxyEntryPoint> entryPoints) => new(entryPoints.Select(e => (JsonNode)JsonValue.Create(e.Name)).ToArray());

    // JSON is YAML, so Traefik's file provider reads it as a .yml file. A container without a port, a cluster without an
    // ingress controller and a name nobody has fall through to the agent, which says why in plain words.
    public static string TraefikDynamic(IReadOnlyList<ProxyEntryPoint> entryPoints, IEnumerable<ProxyRoute> routes, IEnumerable<ClusterIngress> clusters,
        string explainUrl, bool tls, IEnumerable<(string Name, ClusterIngress Ingress)>? published = null)
    {
        var plain = entryPoints.Where(e => !e.Tls).ToList();
        var secure = entryPoints.Where(e => e.Tls).ToList();
        var routers = new JsonObject();
        var services = new JsonObject();

        void Add(string key, string rule, string url, int? priority = null)
        {
            services[key] = new JsonObject { ["loadBalancer"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["url"] = url }) } };
            var router = new JsonObject { ["rule"] = rule, ["entryPoints"] = Names(plain), ["service"] = key };
            if (priority is { } p) router["priority"] = p;
            routers[key] = router;
            if (!tls) return;
            var secured = new JsonObject { ["rule"] = rule, ["entryPoints"] = Names(secure), ["service"] = key, ["tls"] = new JsonObject() };
            if (priority is { } q) secured["priority"] = q;
            routers[key + "-tls"] = secured;
        }

        foreach (var route in routes.OrderBy(r => r.Domain, StringComparer.Ordinal))
            Add("c-" + Key(route.Domain), $"Host(`{route.Domain}`)", route.Url);
        var tcpRouters = new JsonObject();
        var tcpServices = new JsonObject();
        foreach (var cluster in clusters.OrderBy(c => c.Cluster, StringComparer.Ordinal))
        {
            var pattern = $@"^.+\.{System.Text.RegularExpressions.Regex.Escape(cluster.Cluster)}\.{DomainNames.Zone.Replace(".", @"\.")}$";
            if (cluster.Http is { } http)
            {
                var key = "k-" + Key(cluster.Cluster);
                services[key] = new JsonObject { ["loadBalancer"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["url"] = $"http://{http}:80" }) } };
                routers[key] = new JsonObject { ["rule"] = $"HostRegexp(`{pattern}`)", ["entryPoints"] = Names(plain), ["service"] = key };
            }
            if (cluster.Https is { } https)
            {
                var key = "k-" + Key(cluster.Cluster);
                tcpServices[key] = new JsonObject { ["loadBalancer"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = $"{https}:443" }) } };
                tcpRouters[key] = new JsonObject
                {
                    ["rule"] = $"HostSNIRegexp(`{pattern}`)", ["entryPoints"] = Names(secure), ["service"] = key,
                    ["tls"] = new JsonObject { ["passthrough"] = true },
                };
            }
        }
        // Hosts a cluster published through external-dns that point at its ingress, outside the cluster's own subdomain.
        foreach (var (name, ingress) in (published ?? []).DistinctBy(p => p.Name).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var key = "e-" + Key(name);
            if (ingress.Http is { } http)
            {
                services[key] = new JsonObject { ["loadBalancer"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["url"] = $"http://{http}:80" }) } };
                routers[key] = new JsonObject { ["rule"] = $"Host(`{name}`)", ["entryPoints"] = Names(plain), ["service"] = key };
            }
            if (ingress.Https is { } https)
            {
                tcpServices[key] = new JsonObject { ["loadBalancer"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = $"{https}:443" }) } };
                tcpRouters[key] = new JsonObject
                {
                    ["rule"] = $"HostSNI(`{name}`)", ["entryPoints"] = Names(secure), ["service"] = key,
                    ["tls"] = new JsonObject { ["passthrough"] = true },
                };
            }
        }
        Add("explain", "PathPrefix(`/`)", explainUrl, priority: 1);

        var root = new JsonObject { ["http"] = new JsonObject { ["routers"] = routers, ["services"] = services } };
        if (tcpRouters.Count > 0) root["tcp"] = new JsonObject { ["routers"] = tcpRouters, ["services"] = tcpServices };
        if (tls)
            root["tls"] = new JsonObject
            {
                ["stores"] = new JsonObject
                {
                    ["default"] = new JsonObject
                    {
                        ["defaultCertificate"] = new JsonObject { ["certFile"] = $"{ConfigDir}/tls/cert.pem", ["keyFile"] = $"{ConfigDir}/tls/key.pem" },
                    },
                },
            };
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}
