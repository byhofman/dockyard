using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// Until the Dockyard VM exists (sub-project C) the macOS host only follows the active docker context.
[SupportedOSPlatform("macos")]
sealed class MacHost(ICommandRunner runner) : IEngineHost
{
    public IReadOnlyList<EngineInfo> Engines { get; } =
        [new("linux", "Linux", "Docker Engine", "the active docker context", "default", "unix:///var/run/docker.sock")];

    public IReadOnlyList<ComponentInfo> Components { get; } = [new("App", "Dockyard")];

    public bool IsInstalled => true;

    public async Task<bool> IsRunning(string engine) => (await runner.Run("docker", "info", "--format", "{{.ID}}")).ExitCode == 0;
    public Task Start(string engine) => throw new NotSupportedException("Start the engine of the active docker context yourself");
    public Task Stop(string engine) => throw new NotSupportedException("Stop the engine of the active docker context yourself");
    public string ActiveEngine() => "linux";
    public Task UseContext(string engine) => Task.CompletedTask;
    public Task<Version?> Installed(string component) => Task.FromResult(Updates.ParseVersion(Paths.AppVersion));
    public async Task<Release?> Latest(string component) => await Updates.LatestApp();
    public Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components) => Task.FromResult(false);
    public Task<bool> Install(Action<string> log) => Task.FromResult(false);
    public Task Uninstall(Action<string> log) => Task.CompletedTask;
    public void ReleaseResources() { }
}
