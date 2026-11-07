using System.Text.RegularExpressions;

namespace Dockyard.Core;

sealed record ProxySettings(string? Http, string? Https, IReadOnlyList<string> NoProxy)
{
    public bool IsEmpty => Http is null && Https is null;
}

// What the host is set to use, read from where its own apps read it. A proxy auto-config file is not followed: it can
// choose a different proxy per URL, and the engine takes one fixed address.
static partial class HostProxySettings
{
    // The engine's Linux reaches the host's loopback here (see GuestAddresses in the agent).
    public const string HostFromEngine = "198.19.250.254";
    static readonly string[] AlwaysDirect = ["localhost", "127.0.0.1", "::1", "host.docker.internal", "gateway.docker.internal", "198.19.250.0/24", ".dockyard.test"];

    public static ProxySettings? FromEnvironment(Func<string, string?> env)
    {
        string? Value(string name) => env(name) is { Length: > 0 } lower ? lower : env(name.ToUpperInvariant()) is { Length: > 0 } upper ? upper : null;
        var http = Value("http_proxy");
        var https = Value("https_proxy");
        if (http is null && https is null) return null;
        return new ProxySettings(http, https, List(Value("no_proxy"), ','));
    }

    // Output of `scutil --proxy`.
    public static ProxySettings? FromScutil(string output)
    {
        string? Field(string name) => Regex.Match(output, $@"^\s*{name} : (.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
        string? Url(string kind) => Field(kind + "Enable") == "1" && Field(kind + "Proxy") is { } host && Field(kind + "Port") is { } port ? $"http://{host}:{port}" : null;
        var http = Url("HTTP");
        var https = Url("HTTPS");
        if (http is null && https is null) return null;
        var exceptions = ExceptionsBlock().Match(output) is { Success: true } block
            ? block.Groups[1].Value.Split('\n').Select(l => l.Split(" : ", 2)).Where(p => p.Length == 2).Select(p => p[1].Trim()).ToList()
            : [];
        return new ProxySettings(http, https, exceptions);
    }

    // HKCU\...\Internet Settings: ProxyServer is "host:port" for everything, or "http=a:1;https=b:2".
    public static ProxySettings? FromWindows(int enabled, string? server, string? bypass)
    {
        if (enabled == 0 || string.IsNullOrWhiteSpace(server)) return null;
        string? http = null, https = null;
        if (!server.Contains('=')) http = https = Url(server);
        else
            foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2) continue;
                if (pair[0].Equals("http", StringComparison.OrdinalIgnoreCase)) http = Url(pair[1]);
                if (pair[0].Equals("https", StringComparison.OrdinalIgnoreCase)) https = Url(pair[1]);
            }
        if (http is null && https is null) return null;
        return new ProxySettings(http, https, List(bypass, ';').Where(b => b != "<local>").ToList());
    }

    static string Url(string address) => address.Contains("://") ? address : "http://" + address;

    static List<string> List(string? text, char separator) =>
        (text ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // A proxy on the host's own loopback (corporate agents usually sit there) is unreachable by that name from the engine.
    public static ProxySettings ForEngine(ProxySettings proxy) => proxy with
    {
        Http = ThroughHost(proxy.Http),
        Https = ThroughHost(proxy.Https),
        NoProxy = [.. AlwaysDirect, .. proxy.NoProxy.Except(AlwaysDirect)],
    };

    // The ports of the proxies on the host's loopback, which only some ways of reaching the host can get to.
    public static IEnumerable<int> LoopbackPorts(ProxySettings proxy) =>
        new[] { proxy.Http, proxy.Https }
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback ? uri.Port : 0)
            .Where(port => port > 0).Distinct();

    // An engine told to use a proxy it cannot reach can pull nothing, which is worse than going direct.
    public static ProxySettings? WithoutPorts(ProxySettings proxy, IReadOnlyCollection<int> unreachable)
    {
        bool Lost(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback && unreachable.Contains(uri.Port);
        var kept = proxy with { Http = Lost(proxy.Http) ? null : proxy.Http, Https = Lost(proxy.Https) ? null : proxy.Https };
        return kept.IsEmpty ? null : kept;
    }

    static string? ThroughHost(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        return uri.IsLoopback ? new UriBuilder(uri) { Host = HostFromEngine }.Uri.ToString().TrimEnd('/') : url;
    }

    [GeneratedRegex(@"ExceptionsList : <array> \{\n(.*?)\n\s*\}", RegexOptions.Singleline)]
    private static partial Regex ExceptionsBlock();
}
