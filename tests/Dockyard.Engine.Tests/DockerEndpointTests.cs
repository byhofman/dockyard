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

    [Fact]
    public void An_imported_container_keeps_its_settings_and_gets_the_committed_image()
    {
        var inspect = System.Text.Json.Nodes.JsonNode.Parse("""
            {
              "Id": "abcdef123456789",
              "Config": { "Hostname": "abcdef123456", "Image": "nginx", "Env": ["A=1"], "Labels": { "com.docker.compose.project": "p" } },
              "HostConfig": { "Binds": ["data:/data"], "RestartPolicy": { "Name": "always" } },
              "NetworkSettings": { "Networks": { "bridge": {}, "p_default": { "Aliases": ["web"] } } }
            }
            """)!;

        var body = EngineImporter.CreateRequest(inspect, "dockyard-import/web:latest");

        Assert.Equal("dockyard-import/web:latest", body["Image"]!.GetValue<string>());
        Assert.Null(body["Hostname"]);
        Assert.Equal("A=1", body["Env"]![0]!.GetValue<string>());
        Assert.Equal("p", body["Labels"]!["com.docker.compose.project"]!.GetValue<string>());
        Assert.Equal("data:/data", body["HostConfig"]!["Binds"]![0]!.GetValue<string>());
        Assert.Equal("web", body["NetworkingConfig"]!["EndpointsConfig"]!["p_default"]!["Aliases"]![0]!.GetValue<string>());
        Assert.Null(body["NetworkingConfig"]!["EndpointsConfig"]!["bridge"]);
    }

    [Fact]
    public void A_hostname_someone_chose_stays()
    {
        var inspect = System.Text.Json.Nodes.JsonNode.Parse("""{ "Id": "abc", "Config": { "Hostname": "db.local" }, "HostConfig": {} }""")!;
        Assert.Equal("db.local", EngineImporter.CreateRequest(inspect, "i")["Hostname"]!.GetValue<string>());
    }
}
