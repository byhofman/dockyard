using Dockyard.Cli;
using Spectre.Console.Cli;

namespace Dockyard.Cli.Tests;

public sealed class CliTests
{
    static int Run(params string[] args)
    {
        using var h = new CliHarness();
        return CliApp.Create(h.Environment).Run(args);
    }

    [Fact]
    public void Help_and_version_answer_without_an_install()
    {
        Assert.Equal(0, Run("--help"));
        Assert.Equal(0, Run("cluster", "--help"));
        Assert.Equal(0, Run("version"));
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("cluster", "create", "Not_A_Name")]
    [InlineData("cluster", "create", "dev", "--workers", "many")]
    [InlineData("https", "maybe")]
    [InlineData("config", "apply")]
    [InlineData("engine", "start")]
    public void Wrong_arguments_are_refused_before_anything_runs(params string[] args) =>
        Assert.ThrowsAny<CommandAppException>(() => Run(args));

    [Fact]
    public void Cluster_options_are_checked_where_they_are_read()
    {
        Assert.Throws<UsageException>(() => Clusters.Storage("tape"));
        Assert.Equal(Kind.ClusterStorage.Nfs, Clusters.Storage("nfs"));
        Assert.Equal([8080, 8443], Clusters.Ports("8080,8443"));
        Assert.Throws<UsageException>(() => Clusters.Ports("80,eighty"));
        Assert.Throws<UsageException>(() => Clusters.Addons("metrics-server,istio"));
        Assert.Equal(["metrics-server"], Clusters.Addons("metrics-server").Select(a => a.Release));
    }

    [Fact]
    public void Clusters_use_the_Linux_engine_Dockyard_set_up() =>
        Assert.Equal(OperatingSystem.IsWindows() ? "linux" : OperatingSystem.IsMacOS() ? "dockyard" : "default", Clusters.Context);
}
