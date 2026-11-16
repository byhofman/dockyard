using System.Formats.Tar;

namespace Dockyard.Engine;

// Every path of an image read once, so going into a folder costs nothing more. An image never changes, so the tree stays
// right for as long as it is looked at, and a folder's size is the sum of what is under it.
public sealed class FileTree
{
    readonly Node root;

    FileTree(Node root, int files)
    {
        this.root = root;
        Files = files;
    }

    public int Files { get; }
    public long Bytes => root.Size;

    public static async Task<FileTree> ReadAsync(Stream tar, IProgress<long>? bytesRead = null, CancellationToken ct = default)
    {
        var root = new Node("", null);
        var files = 0;
        await using var counted = new CountingStream(tar, bytesRead);
        await using var reader = new TarReader(counted);
        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            var parts = Parts(entry.Name);
            if (parts.Length == 0) continue;
            var node = root;
            foreach (var part in parts) node = node.Child(part);
            node.Entry = ToEntry(entry, parts[^1]);
            if (node.Entry is { IsDirectory: false }) files++;
        }
        root.Total();
        return new FileTree(root, files);
    }

    // The children of a folder, folders first; null when there is no such folder.
    public IReadOnlyList<FileEntry>? List(string directory)
    {
        var node = root;
        foreach (var part in Parts(directory))
            if (node.Children?.GetValueOrDefault(part) is { } child) node = child;
            else return null;
        if (node.Children is null) return node == root || node.Entry?.IsDirectory == true ? [] : null;
        return node.Children.Values
            .Select(c => (c.Entry ?? Folder(c.Name)) with { Size = c.Size })
            .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string[] Parts(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();

    static FileEntry Folder(string name) => new(name, true, 0, null, default, default, 0, 0);

    static FileEntry ToEntry(TarEntry entry, string name) => new(
        name,
        entry.EntryType == TarEntryType.Directory,
        entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile ? entry.Length : 0,
        entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink ? entry.LinkName : null,
        entry.ModificationTime,
        entry.Mode,
        entry.Uid,
        entry.Gid);

    sealed class Node(string name, FileEntry? entry)
    {
        public string Name { get; } = name;
        public FileEntry? Entry { get; set; } = entry;
        public Dictionary<string, Node>? Children { get; private set; }
        public long Size { get; private set; }

        public Node Child(string name)
        {
            Children ??= new(StringComparer.Ordinal);
            if (!Children.TryGetValue(name, out var child)) Children[name] = child = new Node(name, null);
            return child;
        }

        public long Total()
        {
            Size = Children is null ? Entry?.Size ?? 0 : Children.Values.Sum(c => c.Total());
            return Size;
        }
    }

    sealed class CountingStream(Stream inner, IProgress<long>? progress) : Stream
    {
        long read;
        long reported;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        int Count(int n)
        {
            read += n;
            if (progress is not null && (n == 0 || read - reported >= 4 << 20))
            {
                reported = read;
                progress.Report(read);
            }
            return n;
        }
    }
}
