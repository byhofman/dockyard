using System.Formats.Tar;

namespace Dockyard.Engine.Tests;

public sealed class FileTreeAndExportTests
{
    sealed record Item(string Name, TarEntryType Type, string Content = "", string? Link = null);

    static MemoryStream Tar(params Item[] items)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
            foreach (var item in items)
            {
                var entry = new PaxTarEntry(item.Type, item.Name);
                if (item.Link is not null) entry.LinkName = item.Link;
                if (item.Type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(item.Content));
                writer.WriteEntry(entry);
            }
        stream.Position = 0;
        return stream;
    }

    // The archive of / names the root "/" and everything below it with a leading slash.
    static Item[] Image =>
    [
        new("/", TarEntryType.Directory),
        new("/etc/", TarEntryType.Directory),
        new("/etc/hostname", TarEntryType.RegularFile, "contoso\n"),
        new("/etc/app/", TarEntryType.Directory),
        new("/etc/app/settings.json", TarEntryType.RegularFile, "{\"name\":\"Fabrikam\"}"),
        new("/etc/os-release", TarEntryType.SymbolicLink, Link: "../usr/lib/os-release"),
        new("/tmp/", TarEntryType.Directory),
        new("./usr/lib/os-release", TarEntryType.RegularFile, "ID=northwind\n"),
        new("/bin/sh", TarEntryType.HardLink, Link: "/bin/busybox"),
    ];

    [Fact]
    public async Task An_image_is_read_once_into_folders_with_the_size_of_what_is_under_them()
    {
        var tree = await FileTree.ReadAsync(Tar(Image), ct: TestContext.Current.CancellationToken);

        Assert.Equal(["bin", "etc", "tmp", "usr"], tree.List("/")!.Select(e => e.Name));
        Assert.All(tree.List("/")!, e => Assert.True(e.IsDirectory));
        var etc = tree.List("/")!.Single(e => e.Name == "etc");
        Assert.Equal(8 + 19, etc.Size);
        Assert.Equal(["app", "hostname", "os-release"], tree.List("/etc")!.Select(e => e.Name));
        Assert.Equal("../usr/lib/os-release", tree.List("/etc/")!.Single(e => e.Name == "os-release").LinkTarget);
        Assert.Equal(19, tree.List("/etc/app")!.Single().Size);
        // A folder with no entry of its own is still a folder; an empty one lists empty; a file or a missing path is no folder.
        Assert.Equal(["os-release"], tree.List("/usr/lib")!.Select(e => e.Name));
        Assert.Empty(tree.List("/tmp")!);
        Assert.Null(tree.List("/etc/hostname"));
        Assert.Null(tree.List("/nowhere"));
        Assert.Equal(8 + 19 + 13, tree.Bytes);
        Assert.Equal(5, tree.Files);
    }

    [Fact]
    public async Task Reading_reports_how_far_it_got()
    {
        var reported = new List<long>();
        var big = new Item("/var/data.bin", TarEntryType.RegularFile, new string('x', 9 << 20));
        await FileTree.ReadAsync(Tar([big]), new SyncProgress(reported.Add), TestContext.Current.CancellationToken);
        Assert.True(reported.Count >= 2, string.Join(", ", reported));
        Assert.Equal(reported.Order(), reported);
        Assert.True(reported[^1] >= 8 << 20, string.Join(", ", reported));
    }

    sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    [Fact]
    public async Task Export_writes_only_plain_files_and_folders_inside_a_new_folder()
    {
        var parent = Directory.CreateTempSubdirectory("dockyard-export-test-").FullName;
        try
        {
            var target = Path.Combine(parent, "contoso-data");
            var tar = Tar(
                new("volume/", TarEntryType.Directory),
                new("volume/notes.txt", TarEntryType.RegularFile, "hello"),
                new("volume/reports/", TarEntryType.Directory),
                new("volume/reports/q1.csv", TarEntryType.RegularFile, "a,b"),
                new("volume/empty/", TarEntryType.Directory),
                new("volume/escape", TarEntryType.SymbolicLink, Link: "/etc"),
                new("volume/hard", TarEntryType.HardLink, Link: "volume/notes.txt"),
                new("volume/../outside.txt", TarEntryType.RegularFile, "no"),
                new("volume/reports/../../outside2.txt", TarEntryType.RegularFile, "no"));

            var result = await FileExport.ExtractAsync(tar, target, TestContext.Current.CancellationToken);

            Assert.Equal(new ExportResult(2, 8, 4), result);
            Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "notes.txt")));
            Assert.Equal("a,b", File.ReadAllText(Path.Combine(target, "reports", "q1.csv")));
            Assert.True(Directory.Exists(Path.Combine(target, "empty")));
            Assert.False(Path.Exists(Path.Combine(target, "escape")));
            Assert.False(Path.Exists(Path.Combine(target, "hard")));
            Assert.Equal(["contoso-data"], Directory.GetFileSystemEntries(parent).Select(Path.GetFileName));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task Export_never_writes_over_a_file_that_is_already_there()
    {
        var target = Directory.CreateTempSubdirectory("dockyard-export-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(target, "notes.txt"), "mine");
            var tar = Tar(new Item("volume/notes.txt", TarEntryType.RegularFile, "theirs"));
            await Assert.ThrowsAsync<IOException>(() => FileExport.ExtractAsync(tar, target, TestContext.Current.CancellationToken));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "notes.txt")));
        }
        finally { Directory.Delete(target, recursive: true); }
    }
}
