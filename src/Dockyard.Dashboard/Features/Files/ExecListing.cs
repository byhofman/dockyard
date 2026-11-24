using System.Diagnostics;
using System.Globalization;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Files;

// One folder of a running container through its own shell: one stat for every entry, then which are folders and where
// links point. The archive API would send the whole subtree for the same answer.
static class ExecListing
{
    public const string Script = """
        cd -- "$1" || exit 3
        stat -c '%f	%s	%Y	%u	%g	%n' -- .* * 2>/dev/null
        echo '--'
        for f in .* *; do
          [ -d "$f" ] && printf 'D\t%s\n' "$f"
          [ -L "$f" ] && printf 'L\t%s\t%s\n' "$f" "$(readlink -- "$f")"
        done
        exit 0
        """;

    public static string[] Args(string context, string container, string directory) =>
        ["--context", context, "exec", container, "sh", "-c", Script, "sh", directory];

    // Null when the container cannot answer this way (stopped, no shell, no stat): the caller reads the archive instead.
    public static async Task<IReadOnlyList<FileEntry>?> ListAsync(string context, string container, string directory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Containers.DockerCli.Path())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in Args(context, container, directory)) psi.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return null;
            }
            if (process.ExitCode == 3) throw new DirectoryNotFoundException(directory);
            return process.ExitCode == 0 ? Parse(await output) : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public static IReadOnlyList<FileEntry>? Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var split = Array.IndexOf(lines, "--");
        if (split < 0) return null;
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines[(split + 1)..])
        {
            var parts = line.Split('\t');
            if (parts is ["D", var name]) folders.Add(name);
            else if (parts is ["L", var link, var target]) links[link] = target;
        }
        var entries = new List<FileEntry>();
        foreach (var line in lines[..split])
        {
            var parts = line.Split('\t', 6);
            if (parts.Length != 6 || parts[5] is "." or ".." or "" ||
                !uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mode) ||
                !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var size) ||
                !long.TryParse(parts[2], CultureInfo.InvariantCulture, out var mtime) ||
                !int.TryParse(parts[3], CultureInfo.InvariantCulture, out var uid) ||
                !int.TryParse(parts[4], CultureInfo.InvariantCulture, out var gid)) continue;
            var name = parts[5];
            var isDirectory = folders.Contains(name);
            entries.Add(new FileEntry(name, isDirectory, isDirectory ? 0 : size, links.GetValueOrDefault(name),
                DateTimeOffset.FromUnixTimeSeconds(mtime), (UnixFileMode)(mode & 0xFFF), uid, gid));
        }
        return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
