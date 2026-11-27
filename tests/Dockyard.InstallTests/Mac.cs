using System.Text.RegularExpressions;

namespace Dockyard.InstallTests;

// Everything but what needs an administrator happens in a throwaway home that is deleted afterwards. A sudo that refuses keeps
// the silent setup from changing /etc or installing Rosetta; DOCKYARD_INSTALL_ADMIN=1 leaves sudo alone and checks what needs
// an administrator, for a throwaway machine such as a CI runner.
public sealed class MacInstalled() : InstallFixture(InstallOs.MacOS)
{
    public static bool Admin => Gate.Flag("DOCKYARD_INSTALL_ADMIN");
    readonly Dictionary<string, string?> env = [];

    // Short: the docker socket lives under it, and unix socket paths have a limit of 104 characters.
    public string Home { get; } = Path.Combine("/tmp", "dy." + Guid.NewGuid().ToString("N")[..6]);
    public string App => Path.Combine(Home, "Applications", "Dockyard.app");
    public string MacOs => Path.Combine(App, "Contents", "MacOS");
    public string Bin => Path.Combine(Home, ".dockyard", "bin");
    public string Docker => Path.Combine(Bin, "docker");
    public string InstalledCli => Path.Combine(Home, ".dockyard", "cli", "dockyard");
    public string Socket => Path.Combine(Home, ".dockyard", "run", "vm.sock");
    public override IDictionary<string, string?> ChildEnv => env;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        if (!Active) return;
        Directory.CreateDirectory(Path.Combine(Home, "shim"));
        if (!Admin)
        {
            var sudo = Path.Combine(Home, "shim", "sudo");
            await File.WriteAllTextAsync(sudo, "#!/bin/sh\nexit 1\n");
            File.SetUnixFileMode(sudo, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        env["HOME"] = Home;
        env["PATH"] = Path.Combine(Home, "shim") + ":" + Environment.GetEnvironmentVariable("PATH");
        foreach (var name in new[] { "DOCKER_CONFIG", "DOCKER_HOST", "DOCKER_CONTEXT", "XDG_CONFIG_HOME" }) env[name] = null;
        // Homebrew installs for the whole Mac, outside this throwaway home, so the source here is Dockyard.
        await SetupOk("--without", "dashboard", "--no-start", "--source", "dockyard");
    }

    public async Task StopEngine()
    {
        if (!File.Exists(Socket)) return;
        await Sh.Run("nc", ["-U", Socket], ChildEnv, "stop\n", 30);
        await Sh.Until(() => Task.FromResult(!File.Exists(Socket)), "the engine to stop", 60);
    }

    public async Task StartEngine()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(MacOs, "dockyard-host"), ["run", "--config", Path.Combine(Home, ".dockyard", "vm.json")]) { UseShellExecute = false, Environment = { ["HOME"] = Home } });
        await Sh.Until(async () => (await Sh.Run(Docker, ["version"], ChildEnv, null, 30)).Exit == 0, "the engine to start");
    }

    public override async ValueTask DisposeAsync()
    {
        if (!Active) return;
        await StopEngine();
        await Sh.Run("pkill", "-f", Home + "/");
        Directory.Delete(Home, true);
    }

    public async Task<bool> Exists(string path) => await Task.FromResult(File.Exists(path) || Directory.Exists(path));
}

public sealed class MacInstallTests(MacInstalled mac) : IClassFixture<MacInstalled>
{
    [InstallFact(InstallOs.MacOS)]
    public async Task Wrong_arguments_exit_2()
    {
        Assert.Equal(2, (await mac.Setup("--typo")).Exit);
    }

    [InstallFact(InstallOs.MacOS)]
    public async Task Installs_without_the_dashboard_and_a_rerun_keeps_the_choices_and_the_data()
    {
        Assert.True(File.Exists(Path.Combine(mac.MacOs, "Dockyard")), "the tray is the app's executable");
        Assert.NotEmpty(Directory.GetFiles(mac.MacOs, "*.dylib"));
        Assert.True(File.Exists(mac.InstalledCli), "the command is an app of its own, not in the bundle");
        Assert.True(File.Exists(Path.Combine(mac.MacOs, "dockyard-host")));
        Assert.False(File.Exists(Path.Combine(mac.MacOs, "Dockyard Dashboard")));
        Assert.True(File.Exists(mac.Docker));
        var vm = await File.ReadAllTextAsync(Path.Combine(mac.Home, ".dockyard", "vm.json"));
        Assert.Contains(mac.App + "/Contents/Resources/vm/kernel", vm.Replace("\\/", "/"));
        Assert.Contains(Path.Combine(mac.MacOs, "Dockyard"), await File.ReadAllTextAsync(Path.Combine(mac.Home, "Library", "LaunchAgents", "com.byhofman.dockyard.plist")));
        Assert.Equal("dockyard", (await mac.Ok(mac.Docker, "context", "show")).Output);
        Assert.Contains("com.apple.security.virtualization", (await Sh.Run("codesign", "-d", "--entitlements", "-", Path.Combine(mac.MacOs, "dockyard-host"))).Output);
        await mac.Ok(Path.Combine(mac.MacOs, "Dockyard"), "--version");
        await mac.Ok(mac.InstalledCli, "--version");

        var marker = Path.Combine(mac.Home, ".dockyard", "marker");
        var disk = Path.Combine(mac.Home, ".dockyard", "data.img");
        await File.WriteAllTextAsync(marker, "kept");
        var before = (await Sh.Ok("ls", "-i", disk)).Output;
        await mac.SetupOk("--no-start");
        Assert.False(File.Exists(Path.Combine(mac.MacOs, "Dockyard Dashboard")), "still no dashboard");
        Assert.True(File.Exists(marker));
        Assert.Equal(before, (await Sh.Ok("ls", "-i", disk)).Output);
    }
}

public sealed class MacComponentTests(MacInstalled mac) : IClassFixture<MacInstalled>
{
    [InstallFact(InstallOs.MacOS)]
    public async Task The_dashboard_is_added_and_removed_and_the_tray_stays()
    {
        var dashboard = Path.Combine(mac.MacOs, "Dockyard Dashboard");
        await mac.Ok(mac.InstalledCli, "component", "add", "dashboard");
        Assert.True(File.Exists(dashboard));
        await mac.Ok("codesign", "--verify", dashboard);
        await mac.Ok(dashboard, "--version");
        await mac.Ok(mac.InstalledCli, "component", "remove", "dashboard");
        Assert.False(File.Exists(dashboard));
        Assert.True(File.Exists(Path.Combine(mac.MacOs, "Dockyard")));
        Assert.NotEmpty(Directory.GetFiles(mac.MacOs, "*.dylib"));
    }

    [InstallFact(InstallOs.MacOS)]
    public async Task A_component_goes_and_comes_back_and_the_engine_is_required()
    {
        var compose = Path.Combine(mac.Home, ".docker", "cli-plugins", "docker-compose");
        var kind = Path.Combine(mac.Bin, "kind");
        await mac.Ok(mac.InstalledCli, "component", "remove", "compose");
        Assert.False(File.Exists(compose));
        await mac.SetupOk("--no-start");
        Assert.False(File.Exists(compose), "it stays gone on a rerun");
        await mac.Ok(mac.InstalledCli, "component", "add", "compose");
        Assert.True(File.Exists(compose));

        Assert.Matches(@"^kind v", (await mac.Ok(kind, "version")).Output);
        await mac.Ok(mac.InstalledCli, "component", "remove", "kind");
        Assert.False(File.Exists(kind));
        await mac.Ok(mac.InstalledCli, "component", "add", "kind");
        Assert.True(File.Exists(kind));
        await mac.Ok(Path.Combine(mac.Bin, "kubectl"), "version", "--client");
        await mac.Ok(Path.Combine(mac.Bin, "helm"), "version", "--short");

        Assert.Equal(2, (await mac.Setup("--no-start", "--without", "linux-engine")).Exit);
    }
}

public sealed class MacEngineTests(MacInstalled mac) : IClassFixture<MacInstalled>
{
    // Needs a Mac that can run a VM: Apple-silicon CI runners have no nested virtualization.
    [InstallFact(InstallOs.MacOS)]
    public async Task The_engine_boots_keeps_a_volume_and_runs_a_kind_cluster()
    {
        Assert.SkipUnless(Gate.Flag("DOCKYARD_INSTALL_BOOT"), "Set DOCKYARD_INSTALL_BOOT=1 on a Mac that can boot the VM");
        var config = Path.Combine(mac.Home, ".dockyard", "vm.json");
        await File.WriteAllTextAsync(config, Regex.Replace(await File.ReadAllTextAsync(config), @"(""memoryMiB""\s*:\s*)\d+", "${1}2048"));
        await mac.StartEngine();
        await mac.Ok(mac.Docker, "volume", "create", "dockyard-e2e");
        var env = new Dictionary<string, string?>(mac.ChildEnv) { ["PATH"] = mac.Bin + ":" + mac.ChildEnv["PATH"] };
        var created = await Sh.Run(Path.Combine(mac.Bin, "kind"), ["create", "cluster", "--name", "e2e", "--wait", "180s"], env);
        try { Assert.True(created.Exit == 0, created.ToString()); }
        finally { await Sh.Run(Path.Combine(mac.Bin, "kind"), ["delete", "cluster", "--name", "e2e"], env); }
        Assert.Equal("", (await mac.Ok(mac.Docker, "ps", "-aq", "--filter", "label=io.x-k8s.kind.cluster=e2e")).Output);
        await mac.StopEngine();

        await mac.StartEngine();
        await mac.Ok(mac.Docker, "volume", "inspect", "dockyard-e2e");
        await mac.StopEngine();
    }

    [InstallFact(InstallOs.MacOS)]
    public async Task Container_domains_are_routed_and_the_CA_is_trusted_and_name_constrained()
    {
        Assert.SkipUnless(MacInstalled.Admin, "Set DOCKYARD_INSTALL_ADMIN=1 to let setup change system files");
        Assert.Contains("port 15353", await File.ReadAllTextAsync("/etc/resolver/dockyard.internal"));
        // Dockyard uses the login keychain by its path, so this home needs one; made here and unlocked, as a login does.
        var keychain = Path.Combine(mac.Home, "Library", "Keychains", "login.keychain-db");
        Directory.CreateDirectory(Path.GetDirectoryName(keychain)!);
        await Sh.Ok("security", "create-keychain", "-p", "e2e", keychain);
        await Sh.Ok("security", "unlock-keychain", "-p", "e2e", keychain);
        await Sh.Ok("security", "set-keychain-settings", keychain);
        await mac.SetupOk("--no-start", "--without", "dashboard", "--trust-https");

        var ca = Path.Combine(mac.Home, ".dockyard", "ca");
        Assert.True(File.Exists(Path.Combine(ca, "ca.pem")));
        Assert.False(File.Exists(Path.Combine(ca, "ca.key")), "no file holds the key, /Users being shared with the engine");
        await Sh.Ok("security", "find-generic-password", "-s", "Dockyard Local CA key", keychain);
        await Sh.Ok("security", "find-certificate", "-c", "Dockyard Local CA", "/Library/Keychains/System.keychain");
        var text = (await Sh.Ok("openssl", "x509", "-in", Path.Combine(ca, "ca.pem"), "-noout", "-text")).Output;
        Assert.Matches(@"Name Constraints: critical[\s\S]{0,200}DNS:dockyard\.internal", text);
    }
}

public sealed class MacUninstallTests(MacInstalled mac) : IClassFixture<MacInstalled>
{
    [InstallFact(InstallOs.MacOS)]
    public async Task Uninstall_keeps_the_data_and_remove_data_takes_it()
    {
        await mac.Ok(mac.Cli, "uninstall");
        Assert.False(Directory.Exists(mac.App));
        Assert.False(File.Exists(Path.Combine(mac.Home, "Library", "LaunchAgents", "com.byhofman.dockyard.plist")));
        Assert.True(File.Exists(Path.Combine(mac.Home, ".dockyard", "data.img")), "data kept");
        Assert.False(File.Exists(Path.Combine(mac.Home, ".docker", "run", "docker.sock")));
        var config = Path.Combine(mac.Home, ".docker", "config.json");
        Assert.False(File.Exists(config) && (await File.ReadAllTextAsync(config)).Contains("credsStore"), "no credential store left in the config");
        if (MacInstalled.Admin)
        {
            foreach (var path in new[] { "/usr/local/bin/dockyard", "/var/run/docker.sock", "/Library/LaunchDaemons/com.byhofman.dockyard.socket.plist", "/etc/resolver/dockyard.internal" })
                Assert.False(File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null, path);
            Assert.NotEqual(0, (await Sh.Run("security", "find-certificate", "-c", "Dockyard Local CA", "/Library/Keychains/System.keychain")).Exit);
        }

        await mac.SetupOk("--no-start");
        Assert.True(File.Exists(Path.Combine(mac.MacOs, "Dockyard Dashboard")), "a fresh install gets the dashboard");
        await mac.Ok(mac.Cli, "uninstall", "--remove-data");
        Assert.False(Directory.Exists(Path.Combine(mac.Home, ".dockyard")), "data removed");
    }
}
