using Dockyard.Cli;
using Spectre.Console.Cli;

namespace Dockyard.Cli.Tests;

public sealed class CliTests
{
    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--silent", "--with", "kind" }, true)]
    [InlineData(new[] { "--help" }, false)]
    [InlineData(new[] { "--version" }, false)]
    [InlineData(new[] { "cluster", "ls" }, false)]
    [InlineData(new[] { "engine", "ls" }, false)]
    public void Without_a_command_or_with_the_installers_options_it_goes_to_the_Toolbox(string[] args, bool handOver) =>
        Assert.Equal(handOver, Commands.IsHandOver(args));

    static int Run(params string[] args) => CliApp.Create(args).Run(args);

    [Fact]
    public void Help_and_version_answer_without_the_Toolbox()
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
    public void A_file_goes_to_the_Toolbox_inline()
    {
        var args = Arguments.InlineFile(["config", "apply", "--file", "daemon.json", "--engine", "linux"], _ => "{ \"debug\": true }");

        Assert.StartsWith(Arguments.InlinePrefix, args[3], StringComparison.Ordinal);
        Assert.Equal("{ \"debug\": true }", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(args[3][Arguments.InlinePrefix.Length..])));
        Assert.Equal(args, Arguments.InlineFile(args, _ => throw new InvalidOperationException("read twice")));
    }

    [Fact]
    public void The_Toolbox_is_found_next_to_the_command_or_one_folder_up()
    {
        var root = Directory.CreateTempSubdirectory("dockyard-cli-").FullName;
        try
        {
            var exe = OperatingSystem.IsWindows() ? "dockyard-toolbox.exe" : "dockyard-toolbox";
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            Assert.Null(Toolbox.Locate(bin));
            File.WriteAllText(Path.Combine(root, exe), "");
            Assert.Equal(Path.Combine(root, exe), Toolbox.Locate(bin + Path.DirectorySeparatorChar));
            File.WriteAllText(Path.Combine(bin, exe), "");
            Assert.Equal(Path.Combine(bin, exe), Toolbox.Locate(bin));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_command_never_takes_itself_for_the_Toolbox()
    {
        var root = Directory.CreateTempSubdirectory("dockyard-cli-").FullName;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var self = Path.Combine(bin, OperatingSystem.IsWindows() ? "Dockyard.exe" : "Dockyard");
            File.WriteAllText(self, "");
            Assert.Null(Toolbox.Locate(bin, self));
            var toolbox = Path.Combine(root, OperatingSystem.IsWindows() ? "dockyard-toolbox.exe" : "dockyard-toolbox");
            File.WriteAllText(toolbox, "");
            Assert.Equal(toolbox, Toolbox.Locate(bin, self));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Clusters_use_the_Linux_engine_Dockyard_set_up() =>
        Assert.Equal(OperatingSystem.IsWindows() ? "linux" : OperatingSystem.IsMacOS() ? "dockyard" : "default", Clusters.Context);
}
