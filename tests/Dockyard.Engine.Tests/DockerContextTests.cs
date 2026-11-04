using System.Security.Cryptography;
using System.Text;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine.Tests;

public sealed class DockerContextTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dy-ctx-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    void WriteConfig(string currentContext) =>
        File.WriteAllText(Path.Combine(dir, "config.json"), $$"""{"auths":{},"currentContext":"{{currentContext}}"}""");

    void WriteContext(string name, string host)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        var metaDir = Directory.CreateDirectory(Path.Combine(dir, "contexts", "meta", hash)).FullName;
        File.WriteAllText(Path.Combine(metaDir, "meta.json"),
            "{\"Name\":\"" + name + "\",\"Metadata\":{},\"Endpoints\":{\"docker\":{\"Host\":\"" + host + "\",\"SkipTLSVerify\":false}}}");
    }

    static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Uses_current_context_from_config()
    {
        WriteConfig("linux");
        WriteContext("linux", "tcp://127.0.0.1:2375");
        var ctx = DockerContext.Resolve(dir, Env());
        Assert.Equal("linux", ctx.Name);
        Assert.Equal(EndpointKind.Tcp, ctx.Endpoint.Kind);
        Assert.Equal(2375, ctx.Endpoint.Port);
    }

    [Fact]
    public void DOCKER_CONTEXT_overrides_config()
    {
        WriteConfig("linux");
        WriteContext("linux", "tcp://127.0.0.1:2375");
        WriteContext("win", "npipe:////./pipe/docker_windows");
        var ctx = DockerContext.Resolve(dir, Env(("DOCKER_CONTEXT", "win")));
        Assert.Equal("win", ctx.Name);
        Assert.Equal("docker_windows", ctx.Endpoint.Address);
    }

    [Fact]
    public void DOCKER_HOST_selects_default_context()
    {
        WriteConfig("linux");
        var ctx = DockerContext.Resolve(dir, Env(("DOCKER_HOST", "tcp://10.0.0.5:2376")));
        Assert.Equal("default", ctx.Name);
        Assert.Equal("10.0.0.5", ctx.Endpoint.Address);
    }

    [Fact]
    public void Missing_config_falls_back_to_platform_default()
    {
        var ctx = DockerContext.Resolve(dir, Env());
        Assert.Equal("default", ctx.Name);
        Assert.Equal(DockerContext.DefaultHost, ctx.Endpoint.Uri);
    }

    [Fact]
    public void Unknown_context_throws()
    {
        WriteConfig("nope");
        var ex = Assert.Throws<DockerContextException>(() => DockerContext.Resolve(dir, Env()));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Exists_checks_context_metadata()
    {
        WriteContext("linux", "tcp://127.0.0.1:2375");
        WriteConfig("linux");
        Assert.True(DockerContext.Exists("linux", dir));
        Assert.True(DockerContext.Exists("default", dir));
        Assert.False(DockerContext.Exists("win", dir));
        Assert.Equal("linux", DockerContext.CurrentContextName(dir));
    }

    [Fact]
    public void DOCKER_CONFIG_sets_config_dir() =>
        Assert.Equal(dir, DockerContext.ConfigDir(Env(("DOCKER_CONFIG", dir))));
}
