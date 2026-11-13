using Dockyard.Engine;
using Dockyard.Engine.Transport;

namespace Dockyard.Core;

// Where Docker Desktop is on each system. Its engine only runs while the app does, so importing may have to start it.
static class DockerDesktop
{
    public static string App => OperatingSystem.IsMacOS() ? "/Applications/Docker.app"
        : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe")
        : "/opt/docker-desktop";

    public static bool IsInstalled => File.Exists(App) || Directory.Exists(App);

    // Not ~/.docker/run/docker.sock on macOS unless it is Docker Desktop's: Dockyard links its own engine there when it is free.
    public static string? Endpoint(string? ownSocket)
    {
        if (OperatingSystem.IsWindows()) return "npipe:////./pipe/dockerDesktopLinuxEngine";
        var home = Paths.UserProfile;
        var candidates = OperatingSystem.IsMacOS()
            ? new[] { Path.Combine(home, "Library", "Containers", "com.docker.docker", "Data", "docker.raw.sock"), Path.Combine(home, ".docker", "run", "docker.sock") }
            : [Path.Combine(home, ".docker", "desktop", "docker.sock")];
        return candidates.Where(c => new FileInfo(c).LinkTarget != ownSocket).Where(File.Exists).Select(c => "unix://" + c).FirstOrDefault();
    }

    public static void Start()
    {
        if (OperatingSystem.IsMacOS()) Shell.Spawn("/usr/bin/open", "-g", App).Dispose();
        else if (OperatingSystem.IsWindows()) Shell.Spawn(App).Dispose();
        else Shell.Spawn("systemctl", "--user", "start", "docker-desktop").Dispose();
    }

    public static async Task<ImportSummary> Import(string? sourceEndpoint, string targetEndpoint, string? ownSocket, Action<string> log, CancellationToken ct = default)
    {
        var endpoint = sourceEndpoint ?? Endpoint(ownSocket);
        if (sourceEndpoint is null && !await Answers(endpoint, ct))
        {
            if (!IsInstalled) throw new InvalidOperationException("Docker Desktop is not installed here");
            log("Starting Docker Desktop to read from it");
            Start();
            var until = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            do
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                endpoint = Endpoint(ownSocket);
                if (DateTime.UtcNow > until) throw new TimeoutException("Docker Desktop's engine did not answer within 3 minutes; start it and try again");
            }
            while (!await Answers(endpoint, ct));
        }
        using var source = new DockerClient(DockerEndpoint.Parse(endpoint!));
        using var target = new DockerClient(DockerEndpoint.Parse(targetEndpoint));
        await target.PingAsync(ct);
        var result = await new EngineImporter(source, target, log).RunAsync(ct);
        foreach (var line in result.Skipped) log("Skipped " + line);
        log($"Imported {result.Images} images, {result.Volumes} volumes, {result.Containers} containers (stopped) and {result.Networks} networks. Docker Desktop still has everything.");
        return result;
    }

    static async Task<bool> Answers(string? endpoint, CancellationToken ct)
    {
        if (endpoint is null) return false;
        try
        {
            using var client = new DockerClient(DockerEndpoint.Parse(endpoint));
            await client.PingAsync(ct);
            return true;
        }
        catch (Exception e) when (e is EngineUnavailableException or DockerException or System.Net.Sockets.SocketException or IOException) { return false; }
    }
}
