namespace Dockyard.Engine.Transport;

public sealed record DockerEndpoint(EndpointKind Kind, string Address, int Port, string Uri)
{
    const int DefaultTcpPort = 2375;

    public static DockerEndpoint Parse(string uri)
    {
        if (uri.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = uri["npipe://".Length..].Replace('\\', '/').TrimStart('/');
            if (!rest.StartsWith("./pipe/", StringComparison.Ordinal) || rest.Length == "./pipe/".Length)
                throw new FormatException($"Unsupported named pipe address: {uri}");
            return new(EndpointKind.NamedPipe, rest["./pipe/".Length..], 0, uri);
        }
        if (uri.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
            return new(EndpointKind.Unix, uri["unix://".Length..], 0, uri);
        if (uri.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri("http://" + uri["tcp://".Length..]);
            return new(EndpointKind.Tcp, u.Host, u.IsDefaultPort ? DefaultTcpPort : u.Port, uri);
        }
        throw new FormatException($"Unsupported docker endpoint: '{uri}'");
    }
}
