using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// kubectl from the Kubernetes project's own apt or dnf repository, helm from Helm's apt repository or the distribution's
// dnf one, so `apt upgrade` and `dnf upgrade` keep them current. kind has no repository.
[SupportedOSPlatform("linux")]
sealed class LinuxPackages(LinuxRoot root) : IPackageManager
{
    static string? Family => File.Exists("/etc/os-release") ? OsRelease.Parse(File.ReadAllText("/etc/os-release")).PackageManager : null;

    public string Name => Family ?? "apt or dnf";

    public bool Carries(CliTool tool) => tool == Tools.Kubectl || tool == Tools.Helm;

    public Task<bool> IsAvailable() => Task.FromResult(Family is "apt" or "dnf");

    public async Task<Version?> Installed(CliTool tool)
    {
        if (Family == "apt")
        {
            var r = await root.TryRun("dpkg-query", "-W", "-f", "${db:Status-Status} ${Version}", tool.Command);
            return r is { ExitCode: 0 } && r.Output.StartsWith("installed ", StringComparison.Ordinal) ? Updates.ParseVersion(r.Output) : null;
        }
        var rpm = await root.TryRun("rpm", "-q", "--qf", "%{VERSION}", tool.Command);
        return rpm is { ExitCode: 0 } ? Updates.ParseVersion(rpm.Output) : null;
    }

    public async Task<Version?> Available(CliTool tool)
    {
        if (Family == "apt")
            return CandidateVersion((await root.TryRun("env", "LANG=C", "apt-cache", "policy", tool.Command))?.Output ?? "");
        return Updates.ParseVersion((await root.TryRun("dnf", "-q", "repoquery", "--latest-limit=1", "--qf", "%{version}\n", tool.Command))?.Output);
    }

    internal static Version? CandidateVersion(string policy) =>
        policy.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Candidate:", StringComparison.Ordinal)) is { } line
            ? Updates.ParseVersion(line)
            : null;

    // The setup script also takes away a copy Dockyard put in /usr/local/bin, which would come first on the PATH.
    public Task Install(Action<string> log, CliTool tool) =>
        root.RunSetup(log, "add-tool", tool.Command, tool == Tools.Kubectl ? KubernetesMinor : "");

    public Task Remove(Action<string> log, CliTool tool) => root.RunSetup(log, "remove-tool", tool.Command);

    // pkgs.k8s.io has a repository per minor version: Dockyard moves it along with its pin.
    static string KubernetesMinor => Tools.Pinned(Tools.Kubectl).Version is var v ? $"{v.Major}.{v.Minor}" : "";
}

// Dockyard's own copies in /usr/local/bin, where every shell finds them, each with a marker in /etc/dockyard: one without
// a marker is someone else's and stays as it is.
[SupportedOSPlatform("linux")]
sealed class LinuxDockyardSource(ICommandRunner runner, LinuxRoot root) : IDockyardSource
{
    public static string Bin(CliTool tool) => $"/usr/local/bin/{tool.Command}";
    public static string Marker(CliTool tool) => $"/etc/dockyard/{tool.Command}";

    public async Task<Version?> Installed(CliTool tool) =>
        File.Exists(Marker(tool)) && File.Exists(Bin(tool)) ? Updates.ParseVersion((await runner.Run(Bin(tool), tool.VersionArgs)).Output) : null;

    public Release? Pinned(CliTool tool) => Tools.Pinned(tool);

    public async Task Install(Action<string> log, CliTool tool)
    {
        var dir = Directory.CreateTempSubdirectory("dockyard-tool").FullName;
        try
        {
            var file = Path.Combine(dir, tool.Command);
            await Tools.Download(tool, file);
            // Root reads it through a path a normal user's temp folder may keep from it otherwise.
            File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101);
            await root.RunSetup(log, "add-bin", tool.Command, file);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public Task Remove(Action<string> log, CliTool tool) => root.RunSetup(log, "remove-bin", tool.Command);
}
