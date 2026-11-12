using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Dockyard.Agent;

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
