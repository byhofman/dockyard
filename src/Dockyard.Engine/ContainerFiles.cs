using System.Formats.Tar;

namespace Dockyard.Engine;

// Files through the engine's archive API, so it works in stopped containers and in images without a shell or ls.
// The price: listing a folder reads its whole subtree, though only the headers are looked at and nothing is kept.
public static class ContainerFiles
{
    public static string Combine(string directory, string name) => directory.TrimEnd('/') + "/" + name;

    public static string Parent(string path, string root)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length <= root.TrimEnd('/').Length) return root;
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    public static async Task<List<FileEntry>> ListAsync(DockerClient client, string container, string directory, CancellationToken ct = default)
    {
        using var response = await client.GetArchiveAsync(container, directory, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await ListTar(stream, directory, ct);
    }

    // Internal so that the shape of the engine's tar can be tested without an engine.
    public static async Task<List<FileEntry>> ListTar(Stream tar, string directory, CancellationToken ct)
    {
        List<FileEntry> entries = [];
        await using var reader = new TarReader(tar);
        // Asking for /etc gives "etc/", "etc/hosts"; asking for / gives its contents directly.
        var trimmed = directory.TrimEnd('/');
        var top = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            var name = entry.Name.TrimEnd('/');
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            name = name.TrimStart('/');
            if (name is "" or ".") continue;
            if (name == top) continue;
            var relative = top.Length > 0 && name.StartsWith(top + "/", StringComparison.Ordinal) ? name[(top.Length + 1)..] : name;
            if (relative.Contains('/')) continue;
            entries.Add(ToEntry(entry, relative));
        }
        return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static FileEntry ToEntry(TarEntry entry, string name) => new(
        name,
        entry.EntryType == TarEntryType.Directory,
        entry.Length,
        entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink ? entry.LinkName : null,
        entry.ModificationTime,
        entry.Mode,
        entry.Uid,
        entry.Gid);

    public static async Task<FileContent> ReadAsync(DockerClient client, string container, string path, int maxBytes, CancellationToken ct = default)
    {
        using var response = await client.GetArchiveAsync(container, path, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var reader = new TarReader(stream);
        var entry = await reader.GetNextEntryAsync(copyData: false, ct) ?? throw new FileNotFoundException(path);
        var name = entry.Name.TrimEnd('/');
        var meta = ToEntry(entry, name[(name.LastIndexOf('/') + 1)..]);
        if (entry.DataStream is null || meta.IsDirectory) return new FileContent([], false, meta);
        var size = (int)Math.Min(entry.Length, maxBytes);
        var data = new byte[size];
        await entry.DataStream.ReadExactlyAsync(data, ct);
        return new FileContent(data, entry.Length > maxBytes, meta);
    }

    public static async Task SaveToAsync(DockerClient client, string container, string path, Stream destination, CancellationToken ct = default)
    {
        using var response = await client.GetArchiveAsync(container, path, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var reader = new TarReader(stream);
        var entry = await reader.GetNextEntryAsync(copyData: false, ct) ?? throw new FileNotFoundException(path);
        if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(destination, ct);
    }

    // The owner and mode of what it replaces, or of a plain file for a new one.
    public static async Task WriteAsync(DockerClient client, string container, string directory, string name, Stream content, FileEntry? like = null, CancellationToken ct = default)
    {
        // The tar goes through a temporary file: its header needs the length, and the source may not know it.
        var temp = Path.GetTempFileName();
        try
        {
            await using (var writerStream = File.Create(temp))
            {
                await using var writer = new TarWriter(writerStream, TarEntryFormat.Pax, leaveOpen: true);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    Mode = like?.Mode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead),
                    Uid = like?.Uid ?? 0,
                    Gid = like?.Gid ?? 0,
                    ModificationTime = DateTimeOffset.UtcNow,
                };
                var buffered = new MemoryStream();
                if (!content.CanSeek)
                {
                    await content.CopyToAsync(buffered, ct);
                    buffered.Position = 0;
                    entry.DataStream = buffered;
                }
                else entry.DataStream = content;
                await writer.WriteEntryAsync(entry, ct);
            }
            await using var tar = File.OpenRead(temp);
            await client.PutArchiveAsync(container, directory, tar, ct);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
