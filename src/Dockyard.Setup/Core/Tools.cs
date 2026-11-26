using Dockyard.Hosts;

namespace Dockyard.Core;

// A command a package manager can bring: winget on Windows, Homebrew on macOS, apt or dnf on Linux. Where there is none,
// Dockyard puts its own pinned copy in place, when the tool has one.
sealed record CliTool(string Id, string Title, string Command, string[] VersionArgs, string Winget, string Brew, string? Flag = null, string? Note = null)
{
    public ComponentInfo Component => new(Id, Title, Flag: Flag, Note: Note);
    public string FileName => OperatingSystem.IsWindows() ? Command + ".exe" : Command;
}

static class Tools
{
    public static readonly CliTool Docker = new(nameof(Core.Component.DockerCli), "Docker CLI", "docker", ["--version"], "Docker.DockerCLI", "docker");
    public static readonly CliTool Compose = new(nameof(Core.Component.Compose), "Docker Compose", "docker-compose", ["version", "--short"], "Docker.DockerCompose", "docker-compose", "compose");
    public static readonly CliTool Buildx = new(nameof(Core.Component.Buildx), "Docker Buildx", "docker-buildx", ["version"], "Docker.Buildx", "docker-buildx", "buildx");
    public static readonly CliTool Kind = new(nameof(Core.Component.Kind), "kind (Kubernetes in Docker)", "kind", ["version"], "Kubernetes.kind", "kind", "kind",
        "Local Kubernetes clusters in containers, on the Linux engine. The dashboard makes and runs its clusters with it.");
    public static readonly CliTool Kubectl = new(nameof(Core.Component.Kubectl), "kubectl", "kubectl", ["version", "--client"], "Kubernetes.kubectl", "kubernetes-cli", "kubectl",
        "The Kubernetes command line, for kind's clusters and any other.");
    public static readonly CliTool Helm = new(nameof(Core.Component.Helm), "Helm", "helm", ["version", "--short"], "Helm.Helm", "helm", "helm",
        "Kubernetes packages. The dashboard installs Traefik and metrics-server into new clusters with it.");

    public static readonly CliTool[] All = [Docker, Compose, Buildx, Kind, Kubectl, Helm];
    // The tools that are their own download, which Dockyard puts in place itself when no package manager brings them.
    public static readonly CliTool[] Standalone = [Kind, Kubectl, Helm];

    public static CliTool? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    static string Os => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
    static string Arch => OperatingSystem.IsMacOS() ? "arm64" : "amd64";

    public static Release Pinned(CliTool tool) => tool.Id switch
    {
        nameof(Core.Component.Kind) => Core.Pinned.Kind($"kind-{Os}-{Arch}"),
        nameof(Core.Component.Kubectl) => Core.Pinned.Kubectl($"{Os}/{Arch}/{tool.FileName}"),
        nameof(Core.Component.Helm) => Core.Pinned.Helm($"{Os}-{Arch}.{(OperatingSystem.IsWindows() ? "zip" : "tar.gz")}"),
        _ => throw new ArgumentException($"{tool.Title} has no download of its own"),
    };

    static string? ArchiveEntry(CliTool tool) => tool == Helm ? $"{Os}-{Arch}/{tool.FileName}" : null;

    public static async Task Download(CliTool tool, string target)
    {
        var release = Pinned(tool);
        if (ArchiveEntry(tool) is not { } entry)
        {
            await Core.Pinned.Download(release, target);
            return;
        }
        var archive = target + ".archive";
        try
        {
            await Core.Pinned.Download(release, archive);
            Archives.Extract(archive, entry, target);
        }
        finally
        {
            File.Delete(archive);
        }
    }
}
