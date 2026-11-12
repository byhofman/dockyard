using System.Text.RegularExpressions;
using Dockyard.Core;

namespace Dockyard.Hosts;

// Paths and generated files for macOS, kept free of platform annotations so they are testable anywhere.
static class MacLayout
{
    public const string Context = "dockyard";
    public const string LaunchAgentLabel = "com.byhofman.dockyard";
    public static Release Cli => Pinned.Docker("mac", "aarch64", "tgz");
    public static Release Compose => Pinned.Compose("docker-compose-darwin-aarch64");
    public static Release Buildx => Pinned.Buildx("darwin-arm64");
    public static Release CredentialHelperRelease => Pinned.CredentialHelper("osxkeychain", "darwin-arm64");

    public static string Home => Paths.UserProfile;
    public static string DataDir => Path.Combine(Home, ".dockyard");
    public static string BinDir => Path.Combine(DataDir, "bin");
    public static string DockerCli => Path.Combine(BinDir, "docker");
    public static string KindCli => Path.Combine(BinDir, "kind");
    public static string PluginDir => Path.Combine(Home, ".docker", "cli-plugins");
    public static string DockerSocket => Path.Combine(DataDir, "docker.sock");
    public static string ControlSocket => Path.Combine(DataDir, "run", "vm.sock");
    public static string VmConfig => Path.Combine(DataDir, "vm.json");
    public static string PluginLinksDir => Path.Combine(DataDir, "plugin-links");
    public static string PreviousContextFile => Path.Combine(DataDir, "previous-context");
    public static string CredentialStoreMarker => Path.Combine(DataDir, "credential-store");
    public static string EngineConfigFile => Path.Combine(DataDir, "engine", "daemon.json");
    public static string RootRelease => Path.Combine(DataDir, "root-release");
    public static string CredentialHelper => Path.Combine(BinDir, "docker-credential-osxkeychain");
    public const string SystemSocket = "/var/run/docker.sock";
    public static string LoginKeychain => Path.Combine(Home, "Library", "Keychains", "login.keychain-db");
    public const string DomainResolver = "/etc/resolver/dockyard.test";
    public const int DomainDnsPort = 15353;
    public const string SocketDaemonLabel = "com.byhofman.dockyard.socket";
    public const string SocketDaemon = "/Library/LaunchDaemons/" + SocketDaemonLabel + ".plist";
    public static string UserSocketLink => Path.Combine(Home, ".docker", "run", "docker.sock");
    public static string TestcontainersFile => Path.Combine(Home, ".testcontainers.properties");
    public static string TestcontainersLine => $"docker.host=unix://{DockerSocket}";
    public static string UserApplications => Path.Combine(Home, "Applications");

    // Where the app belongs: where it already is when that is an Applications folder, otherwise ~/Applications.
    public static string InstallLocation(string baseDirectory, string userApplications)
    {
        var bundle = Bundle(baseDirectory);
        var folder = bundle is null ? null : Path.GetDirectoryName(bundle);
        return folder == "/Applications" || folder == userApplications ? bundle! : Path.Combine(userApplications, "Dockyard.app");
    }

    // The files of the app that is or will be installed. A folder that is no bundle (a development build) is its own install.
    public static string AppBase => Bundle(AppContext.BaseDirectory) is null
        ? AppContext.BaseDirectory
        : Path.Combine(InstallLocation(AppContext.BaseDirectory, UserApplications), "Contents", "MacOS");

    public static string InstalledExe => Path.Combine(AppBase, "Dockyard");
    public static string Helper => Path.Combine(AppBase, "dockyard-vm");
    public static string Assets => Directory.Exists(Path.Combine(AppBase, "..", "Resources", "vm"))
        ? Path.GetFullPath(Path.Combine(AppBase, "..", "Resources", "vm"))
        : Path.Combine(AppBase, "vm");
    public static string LaunchAgent => Path.Combine(Home, "Library", "LaunchAgents", LaunchAgentLabel + ".plist");

    public static string LaunchAgentPlist(string exe) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{LaunchAgentLabel}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{Escape(exe)}</string>
                <string>--tray</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>ProcessType</key>
            <string>Interactive</string>
        </dict>
        </plist>

        """.ReplaceLineEndings("\n");

    public enum SocketUse { Free, Ours, SomeoneElse }

    // Nothing here takes the path from another Docker app: its link stays, even one that points at nothing while that app is stopped.
    public static SocketUse SystemSocketUse(string? linkTarget, bool exists, string ourSocket) =>
        linkTarget == ourSocket ? SocketUse.Ours
        : linkTarget is null && !exists ? SocketUse.Free
        : SocketUse.SomeoneElse;

    // macOS clears /var/run at every boot, so one link made at install would be gone after the first restart.
    public static string SocketDaemonPlist(string ourSocket)
    {
        var link = $"[ -e {SystemSocket} ] || [ -L {SystemSocket} ] || ln -s {Shell.ShQuote(ourSocket)} {SystemSocket}";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{SocketDaemonLabel}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>/bin/sh</string>
                    <string>-c</string>
                    <string>{Escape(link)}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>

            """.ReplaceLineEndings("\n");
    }

    // The content goes in the script itself: a file of this user's to copy could be changed between writing it and root
    // copying it.
    public static string InstallSocketDaemon(string plist) =>
        $"printf '%s' {Shell.ShQuote(plist)} > {SocketDaemon} && chown root:wheel {SocketDaemon} && chmod 644 {SocketDaemon} && "
        + $"(launchctl bootout system/{SocketDaemonLabel} 2>/dev/null; launchctl bootstrap system {SocketDaemon})";

    public static string RemoveSocketDaemon(string ourSocket) =>
        $"launchctl bootout system/{SocketDaemonLabel} 2>/dev/null; rm -f {SocketDaemon}; "
        + $"if [ \"$(readlink {SystemSocket})\" = {Shell.ShQuote(ourSocket)} ]; then rm -f {SystemSocket}; fi";

    public static string InstallDomainResolver =>
        $"mkdir -p /etc/resolver && printf 'nameserver 127.0.0.1\\nport {DomainDnsPort}\\n' > {DomainResolver}";

    // One administrator prompt: AppleScript runs the shell command with administrator privileges.
    public static string[] AdminShell(string command) =>
        ["-e", $"do shell script \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" with administrator privileges"];

    // /etc/paths.d is read by every account's shells, root's too, and ~/.dockyard/bin is this user's to change. So the PATH
    // gets a folder only root can write, with a small script per command that runs this user's copy for this user alone.
    public const string SharedBinDir = "/usr/local/dockyard/bin";
    public static readonly string[] SharedCommands = ["docker", "kind", "docker-credential-osxkeychain"];

    public static string Wrapper(string uid, string binDir, string command) =>
        $"#!/bin/sh\n[ \"$(id -u)\" = {uid} ] || {{ echo \"{command}: this is Dockyard for another account on this Mac\" >&2; exit 127; }}\nexec {Shell.ShQuote(Path.Combine(binDir, command))} \"$@\"\n";

    public static string PathsEntry(string uid, string binDir) =>
        $"mkdir -p {SharedBinDir} && chown root:wheel /usr/local/dockyard {SharedBinDir} && chmod 755 /usr/local/dockyard {SharedBinDir} && "
        + string.Concat(SharedCommands.Select(c => $"printf '%s' {Shell.ShQuote(Wrapper(uid, binDir, c))} > {SharedBinDir}/{c} && chmod 755 {SharedBinDir}/{c} && "))
        + $"echo {SharedBinDir} > /etc/paths.d/dockyard && chmod 644 /etc/paths.d/dockyard";

    public const string RemovePathsEntry = "rm -f /etc/paths.d/dockyard; rm -rf /usr/local/dockyard";

    public static string ZipAsset(bool engineOnly) => engineOnly ? @"^Dockyard-[\d.]+-macos-engine-only\.zip$" : @"^Dockyard-[\d.]+-macos\.zip$";

    // Everything of the dashboard in the bundle: the tray links none of the dylibs, the engine-only edition leaves them out too.
    public static IEnumerable<string> DashboardFiles(string macosDir) =>
        !Directory.Exists(macosDir) ? [] : Directory.GetFiles(macosDir).Where(f => DashboardNames.Contains(Path.GetFileName(f)) || f.EndsWith(".dylib", StringComparison.Ordinal));

    // The first is what ships now; an install from up to v0.2.1 has the second until its bundle is replaced.
    public static readonly string[] DashboardNames = ["Dockyard Dashboard", "Dockyard.Dashboard"];
    public static bool HasDashboardIn(string macosDir) => DashboardNames.Any(n => File.Exists(Path.Combine(macosDir, n)));

    public static Version? BundleVersion(string infoPlist)
    {
        var m = Regex.Match(infoPlist, @"<key>CFBundleShortVersionString</key>\s*<string>([^<]+)</string>");
        return m.Success ? Updates.ParseVersion(m.Groups[1].Value) : null;
    }

    // Another Docker app's plugin is usually a symlink into its bundle; it is put back on uninstall.
    public static void SetAsideLinkedPlugin(string plugin, string linksDir)
    {
        if (new FileInfo(plugin).LinkTarget is null) return;
        Directory.CreateDirectory(linksDir);
        var kept = Path.Combine(linksDir, Path.GetFileName(plugin));
        if (new FileInfo(kept).LinkTarget is null) File.Move(plugin, kept);
        else File.Delete(plugin);
    }

    public static void RemovePlugin(string plugin, string linksDir)
    {
        if (!Directory.Exists(Path.GetDirectoryName(plugin))) return;
        // A link is another app's, back in its place: Dockyard's own are plain files. Asking twice must not take it away.
        if (new FileInfo(plugin).LinkTarget is not null) return;
        File.Delete(plugin);
        var kept = Path.Combine(linksDir, Path.GetFileName(plugin));
        if (new FileInfo(kept).LinkTarget is not null) File.Move(kept, plugin);
    }

    public const string BundleId = "com.byhofman.dockyard";

    // Everything that is a Dockyard app bundle: where the login item points, either Applications folder, and what an
    // interrupted install or update left next to them. A wizard run from a download only knows its own default otherwise.
    public static IReadOnlyList<string> InstalledBundles(string? launchAgentPlist, string userApplications, string systemApplications = "/Applications")
    {
        List<string> candidates = [];
        foreach (var folder in new[] { userApplications, systemApplications })
            candidates.AddRange(new[] { "Dockyard.app", "Dockyard.app.new", "Dockyard.app.old" }.Select(name => Path.Combine(folder, name)));
        if (launchAgentPlist is not null
            && Regex.Match(launchAgentPlist, @"<string>([^<]+\.app)/Contents/MacOS/Dockyard</string>") is { Success: true } login)
            candidates.Add(login.Groups[1].Value);
        return candidates.Distinct().Where(IsDockyardBundle).ToList();
    }

    static bool IsDockyardBundle(string bundle)
    {
        var plist = Path.Combine(bundle, "Contents", "Info.plist");
        return File.Exists(plist) && File.ReadAllText(plist).Contains($"<string>{BundleId}</string>", StringComparison.Ordinal);
    }

    public static string ContextToRestore(string? saved) => string.IsNullOrWhiteSpace(saved) ? "default" : saved.Trim();

    public static string? Bundle(string baseDirectory)
    {
        var contents = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(baseDirectory));
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return bundle is not null && bundle.EndsWith(".app", StringComparison.Ordinal) ? bundle : null;
    }

    public static string SwapScript(int pid, string newBundle, string bundle) => $"""
        #!/bin/sh
        while kill -0 {pid} 2>/dev/null; do sleep 0.2; done
        rm -rf {Shell.ShQuote(bundle + ".old")}
        mv {Shell.ShQuote(bundle)} {Shell.ShQuote(bundle + ".old")} && mv {Shell.ShQuote(newBundle)} {Shell.ShQuote(bundle)} && rm -rf {Shell.ShQuote(bundle + ".old")}
        open {Shell.ShQuote(bundle)} --args --tray

        """.ReplaceLineEndings("\n");

    public static Version? DockerVersion(string release) => release.Split('\n')
        .Where(l => l.StartsWith("DOCKER_VERSION=", StringComparison.Ordinal))
        .Select(l => Version.TryParse(l["DOCKER_VERSION=".Length..].Trim(), out var v) ? v : null)
        .FirstOrDefault();

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
