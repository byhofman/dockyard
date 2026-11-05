using System.Runtime.Versioning;
using System.Text;
using Dockyard.Core;
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

public sealed class MacInstallTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dockyard-mac-tests").FullName;
    string Plugins => Directory.CreateDirectory(Path.Combine(dir, "cli-plugins")).FullName;
    string Links => Path.Combine(dir, "links");

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string OtherAppPlugin(string content = "other app")
    {
        var inOtherApp = Path.Combine(dir, "OtherApp-docker-compose");
        File.WriteAllText(inOtherApp, content);
        var link = Path.Combine(Plugins, "docker-compose");
        File.CreateSymbolicLink(link, inOtherApp);
        return link;
    }

    static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task A_download_replaces_a_symlink_without_writing_through_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symlinks need a privilege on Windows");
        var link = OtherAppPlugin();

        await Updates.WriteReplacing(Bytes("dockyard"), link);

        Assert.Equal("other app", File.ReadAllText(Path.Combine(dir, "OtherApp-docker-compose")));
        Assert.Null(new FileInfo(link).LinkTarget);
        Assert.Equal("dockyard", File.ReadAllText(link));
        Assert.False(File.Exists(link + ".download"));
    }

    [Fact]
    public async Task A_failed_download_keeps_the_existing_file()
    {
        var target = Path.Combine(dir, "docker");
        File.WriteAllText(target, "working");

        await Assert.ThrowsAsync<IOException>(() => Updates.WriteReplacing(new FailingStream(), target));

        Assert.Equal("working", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".download"));
    }

    [Fact]
    public void Another_apps_plugin_link_comes_back_on_uninstall()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symlinks need a privilege on Windows");
        var link = OtherAppPlugin();

        MacLayout.SetAsideLinkedPlugin(link, Links);
        Assert.False(File.Exists(link));
        File.WriteAllText(link, "dockyard");

        MacLayout.RemovePlugin(link, Links);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("other app", File.ReadAllText(link));
    }

    [Fact]
    public void A_link_to_an_uninstalled_app_is_set_aside_and_restored_too()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symlinks need a privilege on Windows");
        var link = OtherAppPlugin();
        File.Delete(Path.Combine(dir, "OtherApp-docker-compose"));

        MacLayout.SetAsideLinkedPlugin(link, Links);
        Assert.Null(new FileInfo(link).LinkTarget);

        MacLayout.RemovePlugin(link, Links);
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Dockyards_own_plugin_is_just_removed()
    {
        var plugin = Path.Combine(Plugins, "docker-buildx");
        File.WriteAllText(plugin, "dockyard");

        MacLayout.SetAsideLinkedPlugin(plugin, Links);
        Assert.True(File.Exists(plugin));

        MacLayout.RemovePlugin(plugin, Links);
        Assert.False(File.Exists(plugin));
    }

    [Fact]
    public void Removing_plugins_when_the_folder_is_gone_is_fine() =>
        MacLayout.RemovePlugin(Path.Combine(dir, "gone", "docker-compose"), Links);

    [Theory]
    [InlineData("orbstack\n", "orbstack")]
    [InlineData(null, "default")]
    [InlineData(" ", "default")]
    public void Uninstall_returns_to_the_context_from_before_the_install(string? saved, string expected) =>
        Assert.Equal(expected, MacLayout.ContextToRestore(saved));

    [Theory]
    [InlineData("/Volumes/Dockyard/Dockyard.app/Contents/MacOS/", true)]
    [InlineData("/Volumes/Dockyard 1/Dockyard.app/Contents/MacOS/", true)]
    [InlineData("/private/var/folders/x/AppTranslocation/ABC/d/Dockyard.app/Contents/MacOS/", true)]
    [InlineData("/Applications/Dockyard.app/Contents/MacOS/", false)]
    [InlineData("/Volumes/External/Applications/Dockyard.app/Contents/MacOS/", false)]
    public void Install_refuses_to_run_from_a_location_that_goes_away(string baseDirectory, bool temporary) =>
        Assert.Equal(temporary, MacLayout.IsTemporaryLocation(baseDirectory));

    [Fact]
    public void Self_update_downloads_the_offered_release_not_the_running_one()
    {
        Assert.EndsWith("/releases/tags/v2.0.0", Updates.ReleaseApiUrl("1.0.0", "v2.0.0"));
        Assert.EndsWith("/releases/tags/v1.0.0", Updates.ReleaseApiUrl("1.0.0", null));
        Assert.EndsWith("/releases/latest", Updates.ReleaseApiUrl("0.0.0", null));
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task Starting_an_engine_that_already_answers_starts_no_second_helper()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS host");
        var runner = new FakeRunner();

        await new MacHost(runner, ping: () => true).Start(MacHost.Engine);

        Assert.Empty(runner.Commands);
    }

    sealed class FailingStream : MemoryStream
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            throw new IOException("connection lost");
    }
}
