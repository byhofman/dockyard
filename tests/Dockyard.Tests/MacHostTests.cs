using Dockyard.Hosts;

namespace Dockyard.Tests;

public sealed class MacHostTests
{
    [Fact]
    public void Launch_agent_starts_the_tray_at_login()
    {
        var plist = MacLayout.LaunchAgentPlist("/Applications/Dockyard.app/Contents/MacOS/Dockyard");
        Assert.Contains("<string>com.byhofman.dockyard</string>", plist);
        Assert.Contains("<string>/Applications/Dockyard.app/Contents/MacOS/Dockyard</string>\n        <string>--tray</string>", plist.Replace("    ", "    "));
        Assert.Contains("<key>RunAtLoad</key>\n    <true/>", plist);
    }

    [Fact]
    public void Admin_shell_escapes_for_applescript()
    {
        var args = MacLayout.AdminShell(MacLayout.PathsEntry("/Users/me/.dockyard/bin"));
        Assert.Equal("-e", args[0]);
        Assert.Equal("do shell script \"echo '/Users/me/.dockyard/bin' > /etc/paths.d/dockyard && chmod 644 /etc/paths.d/dockyard\" with administrator privileges", args[1]);
        Assert.Contains("\\\"x\\\"", MacLayout.AdminShell("echo \"x\"")[1]);
    }

    [Fact]
    public void Reads_docker_version_from_release_file() =>
        Assert.Equal(new Version(29, 9, 2), MacLayout.DockerVersion("VERSION=1.0.0\nDOCKER_VERSION=29.9.2\n"));
}
