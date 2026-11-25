using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Engine.Transport;

namespace Dockyard.Dashboard.Tests;

public sealed class VolumeAndImageLogicTests
{
    [Fact]
    public void On_Windows_a_volume_of_the_WSL_engine_opens_through_wsl_localhost()
    {
        var at = VolumeLocation.For(HostOs.Windows, "linux", EndpointKind.NamedPipe, false, "contoso-db", "local", "/var/lib/docker/volumes/contoso-db/_data");
        Assert.Equal(@"\\wsl.localhost\dockyard\var\lib\docker\volumes\contoso-db\_data", at.Path);
    }

    [Fact]
    public void On_Windows_a_volume_of_the_Windows_engine_is_its_own_folder()
    {
        var at = VolumeLocation.For(HostOs.Windows, "win", EndpointKind.NamedPipe, true, "iis-logs", "local", @"C:\ProgramData\docker\volumes\iis-logs\_data");
        Assert.Equal(@"C:\ProgramData\docker\volumes\iis-logs\_data", at.Path);
        Assert.Contains("administrators", at.Note);
    }

    [Fact]
    public void On_Linux_a_volume_of_the_local_engine_is_its_folder_and_rootless_ones_too()
    {
        Assert.Equal("/var/lib/docker/volumes/fabrikam/_data",
            VolumeLocation.For(HostOs.Linux, "default", EndpointKind.Unix, false, "fabrikam", "local", "/var/lib/docker/volumes/fabrikam/_data").Path);
        Assert.Equal("/home/contoso/.local/share/docker/volumes/fabrikam/_data",
            VolumeLocation.For(HostOs.Linux, "rootless", EndpointKind.Unix, false, "fabrikam", "local", "/home/contoso/.local/share/docker/volumes/fabrikam/_data").Path);
    }

    [Fact]
    public void On_macOS_there_is_no_folder_and_it_says_so()
    {
        var at = VolumeLocation.For(HostOs.MacOS, "dockyard", EndpointKind.Unix, false, "northwind", "local", "/var/lib/docker/volumes/northwind/_data");
        Assert.Null(at.Path);
        Assert.Contains("VM", at.Note);
        Assert.Contains("Export a copy", at.Note);
    }

    [Theory]
    [InlineData(HostOs.Linux, "default", EndpointKind.Tcp, "local")]
    [InlineData(HostOs.Windows, "remote", EndpointKind.Tcp, "local")]
    [InlineData(HostOs.Windows, "other-wsl", EndpointKind.NamedPipe, "local")]
    [InlineData(HostOs.Linux, "default", EndpointKind.Unix, "nfs")]
    public void Elsewhere_or_another_driver_has_no_folder(HostOs host, string context, EndpointKind endpoint, string driver) =>
        Assert.Null(VolumeLocation.For(host, context, endpoint, false, "data", driver, "/var/lib/docker/volumes/data/_data").Path);

    // Whatever an engine reports, the file manager is only ever sent to <root>/volumes/<that volume>/_data.
    [Theory]
    [InlineData("data", "/var/lib/docker/volumes/data/_data/../../../../etc")]
    [InlineData("data", "/var/lib/docker/volumes/../../../etc/volumes/data/_data")]
    [InlineData("data", "/var/lib/docker/./volumes/data/_data")]
    [InlineData("data", "/var/lib/docker/volumes/other/_data")]
    [InlineData("data", "var/lib/docker/volumes/data/_data")]
    [InlineData("data", @"/var/lib/docker\..\volumes/data/_data")]
    [InlineData("data", "//server/share/volumes/data/_data")]
    [InlineData("..", "/var/lib/docker/volumes/../_data")]
    [InlineData("a/b", "/var/lib/docker/volumes/a/b/_data")]
    [InlineData(".hidden", "/var/lib/docker/volumes/.hidden/_data")]
    public void A_mountpoint_that_could_lead_anywhere_else_is_refused(string name, string mountpoint)
    {
        Assert.Null(VolumeLocation.For(HostOs.Linux, "default", EndpointKind.Unix, false, name, "local", mountpoint).Path);
        Assert.Null(VolumeLocation.For(HostOs.Windows, "linux", EndpointKind.NamedPipe, false, name, "local", mountpoint).Path);
    }

    [Theory]
    [InlineData(@"C:\ProgramData\docker\volumes\data\_data\..\..\..\..\Users")]
    [InlineData(@"C:\ProgramData\..\Windows\volumes\data\_data")]
    [InlineData(@"\\server\share\volumes\data\_data")]
    [InlineData(@"C:\ProgramData\docker\volumes\data\_data:stream")]
    [InlineData(@"C:\ProgramData/docker\volumes\data\_data")]
    public void A_Windows_mountpoint_that_could_lead_anywhere_else_is_refused(string mountpoint) =>
        Assert.Null(VolumeLocation.For(HostOs.Windows, "win", EndpointKind.NamedPipe, true, "data", "local", mountpoint).Path);

    [Fact]
    public void An_image_terminal_on_mac_and_Linux_attaches_and_leaves_a_watcher_behind()
    {
        var command = ExecCommand.ImageTerminalCommand("dockyard", "c0ffee", windows: false, "/Users/contoso/.dockyard/bin/docker");
        Assert.Equal(["sh", "-c"], command.Take(2));
        Assert.Equal(["dockyard-terminal", "/Users/contoso/.dockyard/bin/docker", "dockyard", "c0ffee"], command[3..]);
        Assert.Contains("trap '' HUP INT TERM", command[2]);
        Assert.Contains("rm -f \"$3\"", command[2]);
        Assert.Contains("exec \"$1\" --context \"$2\" start -ai \"$3\"", command[2]);

        var mac = ExecCommand.BuildImageTerminal("dockyard", "c0ffee", "contoso/web", TerminalApp.MacTerminal, "docker");
        Assert.Equal("osascript", mac.File);
        Assert.StartsWith("tell application \"Terminal\" to do script \"sh -c '( trap '\\\\''", mac.Args[1]);
        Assert.EndsWith("dockyard-terminal docker dockyard c0ffee\"", mac.Args[1]);
    }

    [Fact]
    public void An_image_terminal_on_Windows_is_PowerShell_with_a_hidden_watcher_and_quoted_names()
    {
        var command = ExecCommand.BuildImageTerminal("it's", "c0ffee", "contoso/web", new TerminalApp(TerminalKind.WindowsTerminal, @"C:\wt.exe"));
        Assert.Equal(@"C:\wt.exe", command.File);
        Assert.Equal(["-w", "new", "--title", "contoso/web", "--", "powershell", "-NoProfile", "-EncodedCommand"], command.Args.Take(8));
        var main = Decode(command.Args[8]);
        Assert.Contains("& 'docker' --context 'it''s' start -ai 'c0ffee'", main);
        Assert.Contains("Start-Process powershell -WindowStyle Hidden", main);
        var watcher = Decode(System.Text.RegularExpressions.Regex.Match(main, "'-EncodedCommand','([^']+)'").Groups[1].Value);
        Assert.Contains("Wait-Process -Id $parent", watcher);
        Assert.Contains("& 'docker' --context 'it''s' rm -f 'c0ffee'", watcher);
        // Windows Terminal splits its own arguments at ';', which base64 never has.
        Assert.DoesNotContain(';', string.Concat(command.Args));
    }

    [Fact]
    public void From_the_files_viewer_the_file_manager_opens_the_folder_being_looked_at()
    {
        var root = Path.Combine(Path.GetTempPath(), "volumes", "contoso-db", "_data");

        Assert.Equal(root, VolumesViewModel.FolderFor(root, null));
        Assert.Equal(root, VolumesViewModel.FolderFor(root, "/volume"));
        Assert.Equal(Path.Combine(root, "pg", "base"), VolumesViewModel.FolderFor(root, "/volume/pg/base"));
        Assert.Equal(root, VolumesViewModel.FolderFor(root, "/elsewhere/pg"));
    }

    static string Decode(string base64) => System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(base64));
}
