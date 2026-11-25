using Dockyard.Cli;

namespace Dockyard.Cli.Tests;

public sealed class CliTests
{
    [Theory]
    [InlineData(new string[0], "HandOver")]
    [InlineData(new[] { "--silent", "--with", "kind" }, "HandOver")]
    [InlineData(new[] { "--help" }, "Help")]
    [InlineData(new[] { "version" }, "Version")]
    [InlineData(new[] { "cluster", "ls" }, "Cluster")]
    [InlineData(new[] { "config", "edit" }, "ConfigEdit")]
    [InlineData(new[] { "config", "show" }, "Toolbox")]
    [InlineData(new[] { "engine", "ls" }, "Toolbox")]
    [InlineData(new[] { "https", "on" }, "Toolbox")]
    [InlineData(new[] { "update", "--check" }, "Toolbox")]
    public void Each_command_goes_where_it_is_handled(string[] args, string route) => Assert.Equal(route, Commands.RouteOf(args).ToString());

    [Fact]
    public async Task An_unknown_command_is_a_usage_error()
    {
        Assert.Throws<UsageException>(() => Commands.RouteOf(["frobnicate"]));
        await Assert.ThrowsAsync<UsageException>(() => Commands.Run(["cluster"], _ => { }, _ => { }));
    }

    [Fact]
    public void A_file_goes_to_the_Toolbox_inline_and_comes_out_as_it_was()
    {
        var args = Arguments.InlineFile(["config", "apply", "--file", "daemon.json", "--engine", "linux"], _ => "{ \"debug\": true }");

        Assert.StartsWith(Arguments.InlinePrefix, args[3], StringComparison.Ordinal);
        Assert.Equal("{ \"debug\": true }", Arguments.Content(args));
        Assert.Equal(args, Arguments.InlineFile(args, _ => throw new InvalidOperationException("read twice")));
        Assert.Throws<UsageException>(() => Arguments.Option(["--engine"], "--engine"));
    }

    [Fact]
    public void The_Toolbox_is_found_next_to_the_command_or_one_folder_up()
    {
        var root = Directory.CreateTempSubdirectory("dockyard-cli-").FullName;
        try
        {
            var exe = OperatingSystem.IsWindows() ? "Dockyard.exe" : "Dockyard";
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
    public void The_usage_names_every_command()
    {
        foreach (var command in new[] { "engine", "config", "cluster", "https", "update", "version" })
            Assert.Contains($"dockyard {command}", Commands.Usage, StringComparison.Ordinal);
        Assert.Equal(OperatingSystem.IsWindows() ? "linux" : OperatingSystem.IsMacOS() ? "dockyard" : "default", Clusters.Context);
    }
}
