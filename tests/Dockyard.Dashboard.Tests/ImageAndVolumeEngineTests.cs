using System.Diagnostics;
using System.Text.Json.Nodes;
using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Features.Files;
using Dockyard.Engine;
using Dockyard.Engine.Transport;

namespace Dockyard.Dashboard.Tests;

// On a throwaway engine named by the environment: CI's Docker on Linux, Dockyard's WSL engine in the Windows end-to-end
// run. Everything made here is removed again, also when a check fails.
public sealed class ImageAndVolumeEngineTests
{
    const string Image = "alpine:3.24";

    static (DockerClient Client, string Context)? Engine()
    {
        if (Environment.GetEnvironmentVariable("DOCKYARD_TEST_ENDPOINT") is not { Length: > 0 } endpoint) return null;
        var context = Environment.GetEnvironmentVariable("DOCKYARD_TEST_DOCKER_CONTEXT") is { Length: > 0 } c ? c : "default";
        return (new DockerClient(DockerEndpoint.Parse(endpoint)), context);
    }

    [Fact]
    public async Task An_images_files_are_read_through_a_holder_that_cannot_run_and_is_gone_afterwards()
    {
        if (Engine() is not { } engine) { Assert.Skip("No test engine named"); return; }
        using var client = engine.Client;
        var ct = TestContext.Current.CancellationToken;
        await client.PullImageAsync(Image, ct: ct);
        var size = (await client.ListImagesAsync(ct)).First(i => i.RepoTags?.Contains(Image) == true).Size;

        var holder = await client.CreateImageHolderAsync(Image, ct);
        await using (var source = new ImageFileSource(client, holder, size))
        {
            var inspect = await client.GetNodeAsync($"containers/{holder}/json", ct);
            Assert.True(inspect["HostConfig"]!["ReadonlyRootfs"]!.GetValue<bool>());
            Assert.Equal("none", inspect["HostConfig"]!["NetworkMode"]!.GetValue<string>());
            Assert.Equal("created", inspect["State"]!["Status"]!.GetValue<string>());

            var statuses = new List<string>();
            var root = await source.ListAsync("/", new Recorder(statuses), ct);
            Assert.Contains(root, e => e is { Name: "etc", IsDirectory: true, Size: > 0 });
            Assert.Contains(statuses, s => s.StartsWith("Reading the image's files", StringComparison.Ordinal));
            Assert.Contains(await source.ListAsync("/etc", new Recorder([]), ct), e => e is { Name: "passwd", IsDirectory: false });
            var passwd = await source.ReadAsync("/etc/passwd", 1 << 20, ct);
            Assert.StartsWith("root:", System.Text.Encoding.UTF8.GetString(passwd.Data));
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.WriteAsync("/etc", "motd", new MemoryStream(), null, ct));
        }
        await Assert.ThrowsAsync<NotFoundException>(() => client.InspectContainerAsync(holder, ct));
    }

    [Fact]
    public async Task A_volume_opens_read_only_and_its_export_is_a_plain_copy()
    {
        if (Engine() is not { } engine) { Assert.Skip("No test engine named"); return; }
        using var client = engine.Client;
        var ct = TestContext.Current.CancellationToken;
        var volume = "dockyard-test-" + Guid.NewGuid().ToString("N")[..8];
        await client.PostNodeAsync("volumes/create", new JsonObject { ["Name"] = volume }, ct);
        var image = await client.EnsureEmptyImageAsync(ct);
        var export = Directory.CreateTempSubdirectory("dockyard-export-engine-").FullName;
        List<string> holders = [];
        try
        {
            var writable = await client.CreateVolumeHolderAsync(image, volume, "/volume", ct: ct);
            holders.Add(writable);
            using (var content = new MemoryStream("Contoso report"u8.ToArray()))
                await ContainerFiles.WriteAsync(client, writable, "/volume", "report.txt", content, ct: ct);

            var readOnly = await client.CreateVolumeHolderAsync(image, volume, "/volume", readOnly: true, ct: ct);
            holders.Add(readOnly);
            using (var content = new MemoryStream("changed"u8.ToArray()))
                await Assert.ThrowsAnyAsync<DockerException>(() => ContainerFiles.WriteAsync(client, readOnly, "/volume", "report.txt", content, ct: ct));

            using (var response = await client.GetArchiveAsync(readOnly, "/volume/", ct))
            await using (var tar = await response.Content.ReadAsStreamAsync(ct))
                Assert.Equal(new ExportResult(1, 14, 0), await FileExport.ExtractAsync(tar, Path.Combine(export, volume), ct));
            Assert.Equal("Contoso report", File.ReadAllText(Path.Combine(export, volume, "report.txt")));
            Assert.Equal("Contoso report", System.Text.Encoding.UTF8.GetString((await ContainerFiles.ReadAsync(client, writable, "/volume/report.txt", 1024, ct)).Data));

            // On Windows with Dockyard's WSL engine, the folder the Open in Explorer button names is really there.
            if (OperatingSystem.IsWindows() && engine.Context == "linux")
            {
                var mountpoint = (await client.GetNodeAsync($"volumes/{volume}", ct))["Mountpoint"]!.GetValue<string>();
                var at = Features.Resources.VolumeLocation.For(Features.Resources.HostOs.Windows, "linux", EndpointKind.NamedPipe, false, volume, "local", mountpoint);
                Assert.NotNull(at.Path);
                Assert.True(FileManager.CanOpen(at.Path), at.Path);
                Assert.Equal("Contoso report", File.ReadAllText(Path.Combine(at.Path, "report.txt")));
            }
        }
        finally
        {
            foreach (var holder in holders) await client.RemoveContainerAsync(holder, force: true, ct: CancellationToken.None);
            await client.RemoveVolumeAsync(volume, ct: CancellationToken.None);
            await client.RemoveImageAsync(image, ct: CancellationToken.None);
            Directory.Delete(export, recursive: true);
        }
    }

    [Fact]
    public async Task The_shell_is_found_and_an_image_without_one_is_told_apart()
    {
        if (Engine() is not { } engine) { Assert.Skip("No test engine named"); return; }
        using var client = engine.Client;
        var ct = TestContext.Current.CancellationToken;
        await client.PullImageAsync(Image, ct: ct);
        Assert.Equal("/bin/sh", await ImageTerminal.FindShellAsync(client, Image, ct));
        var empty = await client.EnsureEmptyImageAsync(ct);
        try { Assert.Null(await ImageTerminal.FindShellAsync(client, empty, ct)); }
        finally { await client.RemoveImageAsync(empty, ct: CancellationToken.None); }
        Assert.DoesNotContain(await client.ListContainersAsync(all: true, ct), c => c.Labels?.ContainsKey("dockyard.helper") == true && c.Image == empty);
    }

    // What closing the terminal window does to the processes in it: the terminal's process is killed while the shell in
    // the container still runs. The watcher has to remove the container anyway.
    [Fact]
    public async Task A_terminal_container_is_removed_when_its_terminal_is_killed()
    {
        if (Engine() is not { } engine) { Assert.Skip("No test engine named"); return; }
        using var client = engine.Client;
        var context = engine.Context;
        var ct = TestContext.Current.CancellationToken;
        await client.PullImageAsync(Image, ct: ct);
        var id = await ImageTerminal.CreateAsync(client, Image, "/bin/sh", ct);
        Process? terminal = null;
        try
        {
            var command = ExecCommand.ImageTerminalCommand(context, id, OperatingSystem.IsWindows(), "docker");
            terminal = Process.Start(WithTty(command));
            Assert.NotNull(terminal);
            Assert.True(await Until(async () => (await client.InspectContainerAsync(id, ct)).State.Running, ct), "the shell never started");

            terminal.Kill();
            Assert.True(await Until(async () => await client.FindContainerAsync(id, ct) is null, ct), "the container stayed after its terminal was gone");
        }
        finally
        {
            try { terminal?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await client.RemoveContainerAsync(id, force: true, ct: CancellationToken.None); } catch (NotFoundException) { }
        }
    }

    // docker start -ai needs a terminal: script(1) gives it one on macOS and Linux, a console window on Windows.
    static ProcessStartInfo WithTty(List<string> command)
    {
        if (OperatingSystem.IsWindows())
        {
            var windows = new ProcessStartInfo(command[0]) { UseShellExecute = true };
            foreach (var a in command[1..]) windows.ArgumentList.Add(a);
            return windows;
        }
        var info = new ProcessStartInfo("script") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
        if (OperatingSystem.IsMacOS())
        {
            info.ArgumentList.Add("-q");
            info.ArgumentList.Add("/dev/null");
            foreach (var a in command) info.ArgumentList.Add(a);
        }
        else
        {
            info.ArgumentList.Add("-qec");
            info.ArgumentList.Add(string.Join(' ', command.Select(a => "'" + a.Replace("'", "'\\''") + "'")));
            info.ArgumentList.Add("/dev/null");
        }
        return info;
    }

    static async Task<bool> Until(Func<Task<bool>> check, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            try { if (await check()) return true; }
            catch (NotFoundException) { }
            await Task.Delay(500, ct);
        }
        return false;
    }

    sealed class Recorder(List<string> into) : IProgress<string>
    {
        public void Report(string value) => into.Add(value);
    }
}
