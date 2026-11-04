using Dockyard.Engine.Transport;

namespace Dockyard.Engine.Tests;

public sealed class DockerEndpointTests
{
    [Theory]
    [InlineData("npipe:////./pipe/docker_windows", EndpointKind.NamedPipe, "docker_windows", 0)]
    [InlineData("npipe://./pipe/docker_engine", EndpointKind.NamedPipe, "docker_engine", 0)]
    [InlineData("unix:///var/run/docker.sock", EndpointKind.Unix, "/var/run/docker.sock", 0)]
    [InlineData("tcp://127.0.0.1:2375", EndpointKind.Tcp, "127.0.0.1", 2375)]
    [InlineData("tcp://docker-host", EndpointKind.Tcp, "docker-host", 2375)]
    public void Parses_supported_endpoints(string uri, EndpointKind kind, string address, int port)
    {
        var e = DockerEndpoint.Parse(uri);
        Assert.Equal(kind, e.Kind);
        Assert.Equal(address, e.Address);
        Assert.Equal(port, e.Port);
        Assert.Equal(uri, e.Uri);
    }

    [Theory]
    [InlineData("ssh://user@host")]
    [InlineData("npipe://server/pipe/x")]
    [InlineData("")]
    public void Rejects_unsupported_endpoints(string uri) =>
        Assert.Throws<FormatException>(() => DockerEndpoint.Parse(uri));
}
