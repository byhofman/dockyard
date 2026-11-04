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

public sealed class MacUpdateTests
{
    [Fact]
    public void Finds_the_enclosing_app_bundle()
    {
        var app = Path.Combine(Path.GetTempPath(), "Applications", "Dockyard.app");
        Assert.Equal(app, MacLayout.Bundle(Path.Combine(app, "Contents", "MacOS") + Path.DirectorySeparatorChar));
        Assert.Null(MacLayout.Bundle(Path.Combine(Path.GetTempPath(), "Downloads", "Dockyard") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Release_zip_names_per_edition()
    {
        Assert.Matches(MacLayout.ZipAsset(engineOnly: false), "Dockyard-1.2.0-macos.zip");
        Assert.DoesNotMatch(MacLayout.ZipAsset(engineOnly: false), "Dockyard-1.2.0-macos-engine-only.zip");
        Assert.Matches(MacLayout.ZipAsset(engineOnly: true), "Dockyard-1.2.0-macos-engine-only.zip");
    }

    [Fact]
    public void Swap_script_waits_for_the_tray_then_replaces_and_reopens()
    {
        var script = MacLayout.SwapScript(4242, "/tmp/u/Dockyard.app", "/Users/me/Applications/Dockyard.app");
        Assert.Contains("while kill -0 4242", script);
        Assert.Contains("mv '/tmp/u/Dockyard.app' '/Users/me/Applications/Dockyard.app'", script);
        Assert.Contains("open '/Users/me/Applications/Dockyard.app' --args --tray", script);
    }
}
