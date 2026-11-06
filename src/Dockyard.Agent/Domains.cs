using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dockyard.Engine;

namespace Dockyard.Agent;

sealed record DomainTarget(string ContainerId, string ContainerName, string Domain, IPAddress? Address, int? Pid, IReadOnlyList<int> Exposed, int? Label, bool IsHostNetwork);

sealed record DomainInfo(string ContainerId, string Domain, int? Port, bool PortIsChosen);

static class DomainNames
{
    public const string Zone = "dockyard.test";
    public const string PortLabel = "dockyard.port";
    // People moving from OrbStack have this label in their Compose files already.
    public const string OrbStackPortLabel = "dev.orbstack.http-port";

    static string Clean(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return builder.ToString().Trim('-');
    }

    public static bool InZone(string name) => name == Zone || name.EndsWith("." + Zone, StringComparison.Ordinal);

    // A flat name on purpose: a certificate's wildcard covers exactly one label, so web.shop.dockyard.test would need a
    // new certificate per project and the app holding the CA to answer for each.
    public static IEnumerable<string> For(string containerName, string? project, string? service)
    {
        var own = Clean(containerName);
        if (own.Length > 0) yield return $"{own}.{Zone}";
        if (project is { Length: > 0 } && service is { Length: > 0 }) yield return $"{Clean(project)}-{Clean(service)}.{Zone}";
    }

    // Chosen by the user over labelled over detected. Among what is listening or exposed, the usual web ports first,
    // since an app often also opens a debug or metrics port with a lower number.
    static readonly int[] WebPorts = [80, 8080, 3000, 8000, 5000, 4000, 5173, 4200, 8888, 9000];

    public static int? PickPort(int? chosen, int? label, IEnumerable<int> listening, IEnumerable<int> exposed)
    {
        if (chosen is { } c) return c;
        if (label is { } l) return l;
        var heard = listening.ToHashSet();
        var declared = exposed.ToHashSet();
        foreach (var port in WebPorts) if (heard.Contains(port)) return port;
        foreach (var port in WebPorts) if (declared.Contains(port)) return port;
        if (heard.Count > 0) return heard.Min();
        return declared.Count > 0 ? declared.Min() : null;
    }

    public static int? LabelPort(IReadOnlyDictionary<string, string>? labels) =>
        labels is not null && (labels.TryGetValue(PortLabel, out var a) || labels.TryGetValue(OrbStackPortLabel, out a)) && int.TryParse(a, out var port) && port is > 0 and <= 65535 ? port : null;
}

// Names to containers, and which port to use for each, kept up to date from the engine's events.
sealed class DomainTable(Func<DockerClient> connect, string choicesFile, Action<string> log)
{
    readonly object gate = new();
    Dictionary<string, DomainTarget> byDomain = [];
    Dictionary<string, int> chosen = LoadChoices(choicesFile);

    static Dictionary<string, int> LoadChoices(string file)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(file), AgentApiJson.Default.DictionaryStringInt32) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public DomainTarget? Find(string domain)
    {
        lock (gate) return byDomain.GetValueOrDefault(domain);
    }

    public bool Contains(string domain) => Find(domain) is not null;

    // Chosen by name, so it outlives the container: Compose replaces containers whenever their configuration changes.
    public int? Port(DomainTarget target)
    {
        int? choice;
        lock (gate) choice = chosen.TryGetValue(target.ContainerName, out var c) ? c : null;
        return DomainNames.PickPort(choice, target.Label, Listening(target), target.Exposed);
    }

    static IEnumerable<int> Listening(DomainTarget target)
    {
        if (target.Pid is not { } pid) return [];
        List<int> ports = [];
        foreach (var file in new[] { "tcp", "tcp6" })
        {
            try { ports.AddRange(ListeningPorts.Parse(File.ReadAllText($"/proc/{pid}/net/{file}"), udp: false)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return ports;
    }

    public List<DomainInfo> List()
    {
        List<DomainTarget> targets;
        lock (gate) targets = byDomain.Values.ToList();
        return targets.Select(t =>
        {
            bool isChosen;
            lock (gate) isChosen = chosen.ContainsKey(t.ContainerName);
            return new DomainInfo(t.ContainerId, t.Domain, Port(t), isChosen);
        }).OrderBy(d => d.Domain).ToList();
    }

    public void Choose(string containerId, int? port)
    {
        lock (gate)
        {
            var name = byDomain.Values.FirstOrDefault(t => t.ContainerId == containerId)?.ContainerName;
            if (name is null) return;
            if (port is { } p) chosen[name] = p;
            else chosen.Remove(name);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(choicesFile)!);
                File.WriteAllText(choicesFile, JsonSerializer.Serialize(chosen, AgentApiJson.Default.DictionaryStringInt32));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"cannot keep the port choice: {e.Message}"); }
        }
    }

    public async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var docker = connect();
                var since = DateTimeOffset.UtcNow.AddSeconds(-1);
                await Refresh(docker, ct);
                await foreach (var e in docker.EventsAsync(["container"], since, ct))
                    if (e.Action is "start" or "die" or "destroy" or "rename") await Refresh(docker, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await Task.Delay(1000, ct);
            }
        }
    }

    async Task Refresh(DockerClient docker, CancellationToken ct)
    {
        Dictionary<string, DomainTarget> next = [];
        foreach (var summary in (await docker.ListContainersAsync(all: false, ct)).Where(c => c.State == "running"))
        {
            var inspect = await docker.GetNodeAsync($"containers/{Uri.EscapeDataString(summary.Id)}/json", ct);
            var address = inspect["NetworkSettings"]?["Networks"]?.AsObject()
                .Select(n => n.Value?["IPAddress"]?.GetValue<string>())
                .FirstOrDefault(ip => ip is { Length: > 0 });
            var exposed = inspect["Config"]?["ExposedPorts"]?.AsObject()
                .Select(p => p.Key.Split('/'))
                .Where(p => p.Length == 2 && p[1] == "tcp" && int.TryParse(p[0], out _))
                .Select(p => int.Parse(p[0])).ToList() ?? [];
            var hostNetwork = summary.HostConfig?.NetworkMode == "host";
            var labels = summary.Labels;
            foreach (var domain in DomainNames.For(summary.Name, labels?.GetValueOrDefault("com.docker.compose.project"), labels?.GetValueOrDefault("com.docker.compose.service")))
                // The first running replica answers for a Compose service's shared name.
                next.TryAdd(domain, new DomainTarget(summary.Id, summary.Name, domain, address is null ? null : IPAddress.Parse(address),
                    inspect["State"]?["Pid"]?.GetValue<int>(), exposed, DomainNames.LabelPort(labels), hostNetwork));
        }
        lock (gate) byDomain = next;
    }
}

// What a browser reaches on port 80 and 443: finds the container from the Host header or nothing at all.
sealed class DomainProxy(DomainTable table, Action<string> log)
{
    const int MaxHead = 32 * 1024;
    X509Certificate2? certificate;

    public bool HasCertificate => certificate is not null;

    public void SetCertificate(string certPem, string keyPem)
    {
        certificate = X509Certificate2.CreateFromPem(certPem, keyPem);
        log($"certificate for HTTPS, valid until {certificate.NotAfter:yyyy-MM-dd}");
    }

    public static Socket Listen(IPAddress address, int port)
    {
        var listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(address, port));
        listener.Listen(128);
        return listener;
    }

    // guarded: reached through an iptables redirect, on a port that is open on every interface. A connection that was
    // not redirected from port 80 or 443 came straight here, possibly from the LAN, and gets nothing.
    public async Task Serve(Socket listener, bool tls, bool guarded, CancellationToken ct)
    {
        using var owned = listener;
        while (!ct.IsCancellationRequested)
        {
            var client = await listener.AcceptAsync(ct);
            _ = Task.Run(async () =>
            {
                using var connection = new SocketConnection(client);
                try
                {
                    if (guarded && HostProxy.OriginalDestination(client) is not { Port: 80 or 443 }) return;
                    if (tls) await ServeTls(connection, ct);
                    else await ServeHttp(connection, connection.Stream, "http", ct);
                }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException) { }
            }, ct);
        }
    }

    async Task ServeTls(SocketConnection connection, CancellationToken ct)
    {
        if (certificate is not { } current) return;
        await using var tls = new SslStream(connection.Stream, leaveInnerStreamOpen: true);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = current,
            ApplicationProtocols = [SslApplicationProtocol.Http11],
        }, ct);
        await ServeHttp(connection, tls, "https", ct);
    }

    async Task ServeHttp(SocketConnection connection, Stream stream, string scheme, CancellationToken ct)
    {
        var head = await ReadHead(stream, ct);
        if (head is null) return;
        var host = HttpHead.Host(head);
        if (host is null || table.Find(host) is not { } target)
        {
            await Respond(stream, 404, $"No running container is called {host}.\n", ct);
            return;
        }
        if (table.Port(target) is not { } port)
        {
            await Respond(stream, 503, $"{target.ContainerName} is running, but nothing in it listens yet. If it should, choose its port in the Dockyard dashboard.\n", ct);
            return;
        }
        var address = target.IsHostNetwork ? IPAddress.Loopback : target.Address;
        if (address is null)
        {
            await Respond(stream, 503, $"{target.ContainerName} has no network.\n", ct);
            return;
        }
        var upstream = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await upstream.ConnectAsync(address, port, ct);
        }
        catch (SocketException)
        {
            upstream.Dispose();
            await Respond(stream, 502, $"{target.ContainerName} does not answer on port {port}. If that is the wrong port, choose another in the Dockyard dashboard.\n", ct);
            return;
        }
        using var server = new SocketConnection(upstream);
        await server.Stream.WriteAsync(HttpHead.WithForwarded(head, scheme, host), ct);
        var toServer = Task.Run(async () =>
        {
            try { await stream.CopyToAsync(server.Stream, 16 * 1024, ct); }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
            server.ShutdownSend();
        }, ct);
        try { await server.Stream.CopyToAsync(stream, 16 * 1024, ct); }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
        connection.ShutdownSend();
        await toServer;
    }

    static async Task<byte[]?> ReadHead(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHead];
        var length = 0;
        while (length < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(length, 1), ct);
            if (n == 0) return null;
            length++;
            if (length >= 4 && buffer[length - 1] == '\n' && buffer[length - 2] == '\r' && buffer[length - 3] == '\n' && buffer[length - 4] == '\r')
                return buffer[..length];
        }
        return null;
    }

    static async Task Respond(Stream stream, int status, string body, CancellationToken ct)
    {
        var reason = status switch { 404 => "Not Found", 502 => "Bad Gateway", _ => "Service Unavailable" };
        var text = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
    }
}

static class HttpHead
{
    public static string? Host(byte[] head)
    {
        foreach (var line in Encoding.ASCII.GetString(head).Split("\r\n"))
        {
            if (!line.StartsWith("host:", StringComparison.OrdinalIgnoreCase)) continue;
            var host = line[5..].Trim().ToLowerInvariant();
            var colon = host.LastIndexOf(':');
            return colon > 0 ? host[..colon] : host;
        }
        return null;
    }

    public static byte[] WithForwarded(byte[] head, string scheme, string host)
    {
        var text = Encoding.Latin1.GetString(head);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return Encoding.Latin1.GetBytes(text[..end] + $"\r\nX-Forwarded-Proto: {scheme}\r\nX-Forwarded-Host: {host}" + text[end..]);
    }
}

// One JSON request and one JSON reply per line. Only the app and the dashboard get here: over vsock on macOS, on
// loopback in WSL, on a socket for the docker group on a Linux host.
sealed class AgentRequest
{
    public string Op { get; set; } = "";
    public string? Container { get; set; }
    public int? Port { get; set; }
    public string? Cert { get; set; }
    public string? Key { get; set; }
}

sealed class AgentReply
{
    public string? Error { get; set; }
    public List<DomainInfo>? Domains { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentReply))]
[JsonSerializable(typeof(Dictionary<string, int>), TypeInfoPropertyName = "DictionaryStringInt32")]
partial class AgentApiJson : JsonSerializerContext;

sealed class AgentApi(DomainTable table, DomainProxy proxy)
{
    public async Task Handle(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        while (await reader.ReadLineAsync(ct) is { } line)
            await writer.WriteLineAsync(JsonSerializer.Serialize(Reply(line), AgentApiJson.Default.AgentReply));
    }

    public AgentReply Reply(string line)
    {
        try
        {
            var request = JsonSerializer.Deserialize(line, AgentApiJson.Default.AgentRequest);
            switch (request?.Op)
            {
                case "domains": return new AgentReply { Domains = table.List() };
                case "choose-port" when request is { Container: { } container }:
                    table.Choose(container, request.Port);
                    return new AgentReply { Domains = table.List() };
                case "tls" when request is { Cert: { } cert, Key: { } key }:
                    proxy.SetCertificate(cert, key);
                    return new AgentReply();
                default: return new AgentReply { Error = "unknown request" };
            }
        }
        catch (Exception e) when (e is JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return new AgentReply { Error = e.Message };
        }
    }
}

static class InterfaceAddress
{
    public static IPAddress? Of(string name)
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(i => i.Name == name)?.GetIPProperties().UnicastAddresses
                .Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return null;
        }
    }
}

static class AgentApiServer
{
    public static async Task Tcp(AgentApi api, int port, CancellationToken ct)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
        listener.Listen(16);
        await Accept(listener, api, ct);
    }

    public static async Task Unix(AgentApi api, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(16);
        // The same people who may use the engine, whose socket gives them far more than this one does.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        await Agent.Exit("chgrp", ["docker", path]);
        await Accept(listener, api, ct);
    }

    static async Task Accept(Socket listener, AgentApi api, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = await listener.AcceptAsync(ct);
            _ = Task.Run(async () =>
            {
                using var connection = new SocketConnection(client);
                try { await api.Handle(connection.Stream, ct); }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
            }, ct);
        }
    }
}
