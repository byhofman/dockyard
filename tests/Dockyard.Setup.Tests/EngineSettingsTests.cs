using System.Text.Json.Nodes;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

public sealed class EngineSettingsTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dockyard-engine-settings").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void What_the_user_saved_is_what_comes_back_and_Dockyard_adds_its_dns_when_it_writes_the_engine_s_file()
    {
        EngineSettings.Save(EngineSettings.Linux, """{ "log-level": "warn", "features": { "containerd-snapshotter": true } }""", dir);

        var saved = EngineSettings.Load(EngineSettings.Linux, dir);
        var written = JsonNode.Parse(EngineConfig.WithDockyard(saved))!.AsObject();

        Assert.Equal("warn", written["log-level"]!.GetValue<string>());
        Assert.True(written["features"]!["containerd-snapshotter"]!.GetValue<bool>());
        Assert.Equal(EngineConfig.DnsAddress, written["dns"]![0]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{ \"debug\": true }", null)]
    [InlineData("[1, 2]", "one JSON object")]
    [InlineData("{ \"debug\": true, }", "line 1")]
    [InlineData("{\n  \"debug\": tru\n}", "line 2")]
    public void A_problem_says_where(string json, string? expected)
    {
        var problem = EngineSettings.Problem(json);
        if (expected is null) Assert.Null(problem);
        else Assert.Contains(expected, problem);
    }

    [Fact]
    public void Keys_Dockyard_writes_over_are_named()
    {
        Assert.Equal(["dns"], EngineSettings.Overridden("linux", """{ "dns": ["1.1.1.1"], "debug": true }"""));
        Assert.Equal(["hosts"], EngineSettings.Overridden("win", """{ "hosts": ["tcp://0.0.0.0:2375"] }"""));
        Assert.Empty(EngineSettings.Overridden("win", """{ "group": "admins" }"""));
    }

    [Fact]
    public void The_Windows_engine_keeps_its_pipe_and_its_users_whatever_the_edit_says()
    {
        var written = JsonNode.Parse(DaemonJson.WithAccessFor("""{ "hosts": ["tcp://0.0.0.0:2375"], "group": "admins", "debug": true }""", "npipe:////./pipe/docker_windows", "me"))!;

        Assert.Equal(["npipe:////./pipe/docker_windows"], written["hosts"]!.AsArray().Select(h => h!.GetValue<string>()));
        Assert.Equal("admins,docker-users,me", written["group"]!.GetValue<string>());
        Assert.True(written["debug"]!.GetValue<bool>());
    }
}
