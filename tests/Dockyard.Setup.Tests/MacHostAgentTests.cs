using Dockyard.Hosts;
using Xunit;

namespace Dockyard.Setup.Tests;

public class MacHostAgentTests
{
    [Fact]
    public void Launchd_runs_the_helper_and_restarts_it_only_after_a_failure()
    {
        var plist = Squeeze(MacLayout.HostAgentPlist("/Applications/Dockyard.app/Contents/MacOS/dockyard-host", "/Users/a/.dockyard/vm.json", atLogin: true));

        Assert.Contains("<string>com.byhofman.dockyard.host</string>", plist);
        Assert.Contains("<string>/Applications/Dockyard.app/Contents/MacOS/dockyard-host</string><string>run</string>", plist);
        Assert.Contains("<key>RunAtLoad</key><true/>", plist);
        Assert.Contains("<key>SuccessfulExit</key><false/>", plist);
        Assert.Contains("<key>RunAtLoad</key><false/>", Squeeze(MacLayout.HostAgentPlist("/h", "/c", atLogin: false)));
    }

    static string Squeeze(string xml) => string.Concat(xml.Split('\n').Select(l => l.Trim()));

    [Fact]
    public void A_changed_definition_is_loaded_again_before_the_start()
    {
        Assert.StartsWith("launchctl bootout gui/$(id -u)/com.byhofman.dockyard.host", MacLayout.StartHost(changed: true));
        Assert.StartsWith("launchctl bootstrap gui/$(id -u) '", MacLayout.StartHost(changed: false));
        Assert.EndsWith("launchctl kickstart gui/$(id -u)/com.byhofman.dockyard.host", MacLayout.StartHost(changed: false));
    }
}
