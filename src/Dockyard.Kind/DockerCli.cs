namespace Dockyard.Kind;

static class DockerCli
{
    public static string Path(string? home = null) =>
        CliLocator.Find("docker", File.Exists, Environment.GetEnvironmentVariable("PATH"), home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows() ? CliLocator.WingetPackages : null) ?? "docker";
}
