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

    static MemoryStream Tar(params (string Name, bool Directory)[] entries)
    {
        var stream = new MemoryStream();
        using (var writer = new System.Formats.Tar.TarWriter(stream, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
            foreach (var (name, directory) in entries)
                writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(directory ? System.Formats.Tar.TarEntryType.Directory : System.Formats.Tar.TarEntryType.RegularFile, name));
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task A_folder_lists_only_what_is_directly_in_it_folders_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = await ContainerFiles.ListTar(Tar(("etc/", true), ("etc/hosts", false), ("etc/apk/", true), ("etc/apk/repositories", false)), "/etc", ct);
        Assert.Equal(["apk", "hosts"], entries.Select(e => e.Name));
        Assert.True(entries[0].IsDirectory);
    }

    [Fact]
    public async Task The_root_lists_its_contents_directly()
    {
        var entries = await ContainerFiles.ListTar(Tar(("bin/", true), ("bin/sh", false), ("etc/", true), ("etc/hosts", false)), "/", TestContext.Current.CancellationToken);
        Assert.Equal(["bin", "etc"], entries.Select(e => e.Name));
    }

    [Fact]
    public async Task The_root_may_come_with_leading_slashes_and_dot_files_stay()
    {
        var entries = await ContainerFiles.ListTar(Tar(("//", true), ("//etc/", true), ("//etc/hosts", false), ("/.dockerenv", false)), "/", TestContext.Current.CancellationToken);
        Assert.Equal(["etc", ".dockerenv"], entries.Select(e => e.Name));
    }

    [Theory]
    [InlineData("/etc/apk", "/", "/etc")]
    [InlineData("/etc", "/", "/")]
    [InlineData("/", "/", "/")]
    [InlineData("/volume/sub", "/volume", "/volume")]
    [InlineData("/volume", "/volume", "/volume")]
    public void Up_stops_at_the_root_it_was_given(string path, string root, string expected) =>
        Assert.Equal(expected, ContainerFiles.Parent(path, root));

    [Theory]
    [InlineData("tcp://127.0.0.1:2375", true)]
    [InlineData("unix:///var/run/docker.sock", true)]
    [InlineData("unix:///Users/a/.dockyard/docker.sock", true)]
    [InlineData("unix:///Users/a/.orbstack/run/docker.sock", false)]
    [InlineData("npipe:////./pipe/docker_windows", false)]
    [InlineData("tcp://10.0.0.5:2375", false)]
    public void Only_engines_of_dockyards_own_have_an_agent(string endpoint, bool has) =>
        Assert.Equal(has, AgentClient.For(DockerEndpoint.Parse(endpoint)) is not null);
}
