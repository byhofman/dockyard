using System.Text.Json.Nodes;
using Dockyard.Core;

namespace Dockyard.Hosts;

// The system's own way of installing a tool, so that the tool updates the way the user updates everything else.
interface IPackageManager
{
    string Name { get; }
    Task<bool> IsAvailable();
    bool Carries(CliTool tool) => true;
    // The version this package manager put in place, or null when it did not install the tool.
    Task<Version?> Installed(CliTool tool);
    // The version it would install or upgrade to.
    Task<Version?> Available(CliTool tool);
    // Installs the tool, or upgrades it when it is there.
    Task Install(Action<string> log, CliTool tool);
    Task Remove(Action<string> log, CliTool tool);
}

// winget's portable packages for the whole machine, in Program Files where only administrators write: Dockyard runs it in
// its administrator step, and excludes exactly those files from Defender's rule against files too new to be common, as it
// does for its own. A tool Windows still will not run counts as not installed, and Dockyard's own copy takes its place.
sealed class Winget(ICommandRunner runner, Action<IReadOnlyList<string>>? exclude = null) : IPackageManager
{
    // Success as winget reports it: nothing newer to install, already installed, or not installed at all.
    const int NoApplicableUpgrade = unchecked((int)0x8A15002B);
    const int AlreadyInstalled = unchecked((int)0x8A150061);
    const int NotInstalled = unchecked((int)0x8A150014);

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static readonly string[] LinkDirs = [Path.Combine(ProgramFiles, "WinGet", "Links")];
    public static readonly string MachinePackages = Path.Combine(ProgramFiles, "WinGet", "Packages");
    static readonly string[] UserLinkDirs = [Path.Combine(LocalAppData, "Microsoft", "WinGet", "Links")];
    static readonly string[] UserPackageDirs = [Path.Combine(LocalAppData, "Microsoft", "WinGet", "Packages")];

    // The App Installer alias: an elevated process does not always have WindowsApps on its PATH.
    static string Exe => Path.Combine(LocalAppData, "Microsoft", "WindowsApps", "winget.exe") is var alias && File.Exists(alias) ? alias : "winget";

    public string Name => "winget";

    public static string? Command(CliTool tool) => Command(tool, LinkDirs, [MachinePackages]);

    // One an earlier Dockyard installed for the user, or the user did: it takes over once the machine's is in place.
    public static string? UserCommand(CliTool tool) => Command(tool, UserLinkDirs, UserPackageDirs);

    // A link where winget could make one; the file in the package's own folder, which has the command's name too, otherwise.
    internal static string? Command(CliTool tool, IEnumerable<string> linkDirs, IEnumerable<string> packageDirs) =>
        linkDirs.Select(d => Path.Combine(d, tool.Command + ".exe")).FirstOrDefault(File.Exists)
        ?? Files(tool, packageDirs).FirstOrDefault();

    static IEnumerable<string> Files(CliTool tool, IEnumerable<string> packageDirs) => packageDirs.Where(Directory.Exists)
        .SelectMany(d => Directory.EnumerateDirectories(d, tool.Winget + "_*"))
        .SelectMany(d => Directory.EnumerateFiles(d, tool.Command + ".exe", SearchOption.AllDirectories));

    // What Defender has to let run: the files themselves, which the links point at.
    public static IReadOnlyList<string> MachineFiles(CliTool tool) => Files(tool, [MachinePackages]).ToList();

    public async Task<bool> IsAvailable()
    {
        try { return (await runner.Run(Exe, "--version")).ExitCode == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public Task<Version?> Installed(CliTool tool) => VersionOf(Command(tool), tool);

    async Task<Version?> VersionOf(string? command, CliTool tool)
    {
        if (command is null) return null;
        try { return Updates.ParseVersion((await runner.Run(command, tool.VersionArgs)).Output); }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public async Task<Version?> Available(CliTool tool) =>
        VersionAfterId((await runner.Run(Exe, "search", "--id", tool.Winget, "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity")).Output, tool.Winget);

    public async Task Install(Action<string> log, CliTool tool)
    {
        log($"{tool.Title}: winget install --scope machine {tool.Winget}");
        var r = await runner.Run(Exe, "install", "--id", tool.Winget, "--exact", "--source", "winget", "--scope", "machine", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity");
        if (r.ExitCode is not (0 or NoApplicableUpgrade or AlreadyInstalled))
            throw new InvalidOperationException($"winget could not install {tool.Winget} ({r.ExitCode:X8}): {LastLine(r)}");
        if (Command(tool) is null) throw new InvalidOperationException($"winget installed {tool.Winget} but left no {tool.Command} command");
        exclude?.Invoke(MachineFiles(tool));
        // Defender takes a moment to apply a new exclusion.
        for (var attempt = 0; await Installed(tool) is null; attempt++)
        {
            if (attempt == 5) throw new InvalidOperationException($"Windows does not let winget's {tool.Command} run, likely Defender's rule against files too new to be common");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    public async Task Remove(Action<string> log, CliTool tool)
    {
        var r = await runner.Run(Exe, "uninstall", "--id", tool.Winget, "--exact", "--source", "winget", "--scope", "machine", "--silent",
            "--accept-source-agreements", "--disable-interactivity");
        if (r.ExitCode is not (0 or NotInstalled))
            throw new InvalidOperationException($"winget could not remove {tool.Winget} ({r.ExitCode:X8}): {LastLine(r)}");
        log($"Removed {tool.Title} (winget {tool.Winget})");
    }

    // The user's copy, from a process without administrator rights, which is the only kind winget lets remove it.
    public async Task RemoveUserCopy(Action<string> log, CliTool tool)
    {
        if (UserCommand(tool) is null) return;
        var r = await runner.Run(Exe, "uninstall", "--id", tool.Winget, "--exact", "--source", "winget", "--scope", "user", "--silent",
            "--accept-source-agreements", "--disable-interactivity");
        if (r.ExitCode is 0 or NotInstalled) log($"{tool.Title}: the copy winget installed for you makes way for the machine's");
        else log($"{tool.Title}: winget could not remove the copy it installed for you ({r.ExitCode:X8}); the machine's comes first on the PATH");
    }

    // winget's tables are in the display language, so the version is taken from what follows the id on its row.
    internal static Version? VersionAfterId(string output, string id)
    {
        foreach (var line in output.Split('\n', '\r'))
        {
            var at = line.IndexOf(" " + id + " ", StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return Updates.ParseVersion(line[(at + id.Length + 2)..]);
        }
        return null;
    }

    static string LastLine(ShellResult r) => (r.Output + "\n" + r.Error).Split('\n', '\r').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
}

// Homebrew, run as the user like every brew command has to be.
sealed class Homebrew(ICommandRunner runner, string? prefix = null) : IPackageManager
{
    public static readonly string[] Prefixes = ["/opt/homebrew", "/usr/local"];

    public static string? FindPrefix() => Prefixes.FirstOrDefault(p => File.Exists(Path.Combine(p, "bin", "brew")));

    readonly string? prefix = prefix ?? FindPrefix();

    public string Name => "Homebrew";
    public string? Prefix => prefix;
    string Brew => Path.Combine(prefix!, "bin", "brew");
    public string? Bin(CliTool tool) => prefix is null ? null : Path.Combine(prefix, "bin", tool.Command);
    // Where its docker-compose and docker-buildx formulae put their plugins for the docker CLI to find.
    public string? CliPlugins => prefix is null ? null : Path.Combine(prefix, "lib", "docker", "cli-plugins");

    public Task<bool> IsAvailable() => Task.FromResult(prefix is not null);

    public async Task<Version?> Installed(CliTool tool) =>
        prefix is not null && Directory.Exists(Path.Combine(prefix, "Cellar", tool.Brew)) && File.Exists(Bin(tool))
            ? Updates.ParseVersion((await runner.Run(Bin(tool)!, tool.VersionArgs)).Output)
            : null;

    public async Task<Version?> Available(CliTool tool)
    {
        var r = await runner.Run(Brew, "info", "--json=v2", tool.Brew);
        return r.ExitCode == 0 ? StableVersion(r.Output) : null;
    }

    public async Task Install(Action<string> log, CliTool tool)
    {
        log($"{tool.Title}: brew install {tool.Brew}");
        // brew install upgrades a formula that is already there.
        var r = await runner.Run(Brew, "install", tool.Brew);
        if (r.ExitCode != 0) throw new InvalidOperationException($"brew could not install {tool.Brew}: {(r.Error + r.Output).Trim().Split('\n').Last()}");
    }

    public async Task Remove(Action<string> log, CliTool tool)
    {
        if (await Installed(tool) is null) return;
        var r = await runner.Run(Brew, "uninstall", tool.Brew);
        if (r.ExitCode != 0) throw new InvalidOperationException($"brew could not remove {tool.Brew}: {(r.Error + r.Output).Trim().Split('\n').Last()}");
        log($"Removed {tool.Title} (brew {tool.Brew})");
    }

    internal static Version? StableVersion(string json)
    {
        try
        {
            return Updates.ParseVersion(JsonNode.Parse(json)?["formulae"]?[0]?["versions"]?["stable"]?.GetValue<string>());
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
