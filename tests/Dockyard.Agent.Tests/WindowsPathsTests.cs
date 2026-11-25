using System.Text;
using System.Text.Json.Nodes;
using Dockyard.Agent;

namespace Dockyard.Agent.Tests;

public sealed class WindowsPathsTests
{
    [Theory]
    [InlineData(@"C:\Users\me\src:/src", "/c/Users/me/src:/src")]
    [InlineData(@"d:/work:/work:ro", "/d/work:/work:ro")]
    [InlineData(@"C:\:/host", "/c:/host")]
    [InlineData("/c/Users/me:/src", "/c/Users/me:/src")]
    [InlineData("data:/var/lib/data", "data:/var/lib/data")]
    public void A_Windows_bind_source_becomes_the_engines_drive_path(string bind, string expected) =>
        Assert.Equal(expected, WindowsPaths.RewriteBind(bind));

    [Fact]
    public void A_creation_gets_its_binds_and_bind_mounts_rewritten_and_nothing_else()
    {
        var body = Encoding.UTF8.GetBytes("""
            { "Image": "nginx", "HostConfig": { "Binds": ["C:\\src:/src", "cache:/cache"],
              "Mounts": [ { "Type": "bind", "Source": "C:\\data", "Target": "/data" }, { "Type": "volume", "Source": "v", "Target": "/v" } ] } }
            """);

        var rewritten = JsonNode.Parse(WindowsPaths.RewriteCreate(body)!)!;

        Assert.Equal(["/c/src:/src", "cache:/cache"], rewritten["HostConfig"]!["Binds"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.Equal("/c/data", rewritten["HostConfig"]!["Mounts"]![0]!["Source"]!.GetValue<string>());
        Assert.Equal("v", rewritten["HostConfig"]!["Mounts"]![1]!["Source"]!.GetValue<string>());
        Assert.Null(WindowsPaths.RewriteCreate(Encoding.UTF8.GetBytes("""{ "Image": "nginx", "HostConfig": { "Binds": ["/c/src:/src"] } }""")));
        Assert.Null(WindowsPaths.RewriteCreate("not json"u8.ToArray()));
    }

    [Fact]
    public async Task Requests_pass_on_one_after_another_with_only_the_creation_changed()
    {
        const string create = """{"HostConfig":{"Binds":["C:\\src:/src"]}}""";
        var input = "GET /v1.47/version HTTP/1.1\r\nHost: docker\r\n\r\n"
            + $"POST /v1.47/containers/create?name=web HTTP/1.1\r\nHost: docker\r\nContent-Type: application/json\r\nContent-Length: {create.Length}\r\n\r\n{create}"
            + "POST /v1.47/images/load HTTP/1.1\r\nHost: docker\r\nContent-Length: 5\r\n\r\nC:\\x:"
            + "POST /v1.47/containers/abc/attach?stream=1 HTTP/1.1\r\nHost: docker\r\nUpgrade: tcp\r\nConnection: Upgrade\r\n\r\nC:\\raw bytes";
        var output = new MemoryStream();

        await WindowsPaths.CopyRequests(new MemoryStream(Encoding.Latin1.GetBytes(input)), output, TestContext.Current.CancellationToken);

        var result = Encoding.Latin1.GetString(output.ToArray());
        const string expected = """{"HostConfig":{"Binds":["/c/src:/src"]}}""";
        Assert.Contains($"Content-Length: {expected.Length}\r\n\r\n{expected}POST /v1.47/images/load", result, StringComparison.Ordinal);
        Assert.StartsWith("GET /v1.47/version HTTP/1.1\r\nHost: docker\r\n\r\nPOST /v1.47/containers/create?name=web", result, StringComparison.Ordinal);
        Assert.EndsWith("Content-Length: 5\r\n\r\nC:\\x:POST /v1.47/containers/abc/attach?stream=1 HTTP/1.1\r\nHost: docker\r\nUpgrade: tcp\r\nConnection: Upgrade\r\n\r\nC:\\raw bytes", result, StringComparison.Ordinal);
    }
}
