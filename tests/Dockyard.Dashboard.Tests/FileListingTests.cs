using System.Formats.Tar;
using System.Net;
using Dockyard.Dashboard.Features.Files;
using Dockyard.Engine;
using Dockyard.Engine.Transport;

namespace Dockyard.Dashboard.Tests;

public sealed class FileListingTests
{
    [Fact]
    public void A_folder_listed_through_the_containers_shell_has_folders_first_and_links_with_their_target()
    {
        var output = string.Join('\n',
            "41ed\t4096\t1795434462\t0\t0\t.",
            "41ed\t4096\t1795439760\t0\t0\t..",
            "81a4\t208\t1785715200\t0\t0\thosts",
            "41ed\t4096\t1785715200\t0\t0\tapt",
            "a1ff\t12\t1785715200\t0\t0\tmtab",
            "a1ff\t9\t1785715200\t0\t0\tssl-certs",
            "81ed\t70\t1785715200\t1000\t1000\twith space.sh",
            "--",
            "D\t.",
            "D\t..",
            "D\tapt",
            "D\tssl-certs",
            "L\tmtab\t/proc/self/mounts",
            "L\tssl-certs\t../usr/share/certs",
            "");

        var entries = ExecListing.Parse(output)!;

        Assert.Equal(["apt", "ssl-certs", "hosts", "mtab", "with space.sh"], entries.Select(e => e.Name));
        Assert.True(entries[1].IsDirectory);
        Assert.Equal("../usr/share/certs", entries[1].LinkTarget);
        Assert.Equal("/proc/self/mounts", entries[3].LinkTarget);
        Assert.Equal(208, entries[2].Size);
        Assert.Equal(0, entries[0].Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785715200), entries[2].Modified);
        Assert.Equal((UnixFileMode)0x1ED, entries[4].Mode);
        Assert.Equal(1000, entries[4].Uid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sh: stat: not found\n")]
    public void Output_without_the_listing_is_not_a_listing(string output) => Assert.Null(ExecListing.Parse(output));

    [Fact]
    public void The_folder_goes_to_the_script_as_an_argument_never_inside_it() =>
        Assert.Equal(["--context", "linux", "exec", "abc", "sh", "-c", ExecListing.Script, "sh", "/a b/$(x)"], ExecListing.Args("linux", "abc", "/a b/$(x)"));

    sealed class ArchiveHandler(byte[] tar) : HttpMessageHandler
    {
        public int Archives { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/_ping", StringComparison.Ordinal))
            {
                var ping = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
                ping.Headers.Add("Api-Version", "1.47");
                return Task.FromResult(ping);
            }
            Archives++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tar) });
        }
    }

    static byte[] Tar(params string[] paths)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
            foreach (var path in paths)
                writer.WriteEntry(new PaxTarEntry(path.EndsWith('/') ? TarEntryType.Directory : TarEntryType.RegularFile, path));
        return stream.ToArray();
    }

    [Fact]
    public async Task Without_the_shell_the_archive_is_read_once_and_refresh_reads_it_again()
    {
        var handler = new ArchiveHandler(Tar("etc/", "etc/hosts", "etc/apt/", "etc/apt/sources.list", "bin/", "bin/sh"));
        using var client = new DockerClient(DockerEndpoint.Parse("unix:///nonexistent.sock"), handler);
        var shellTries = 0;
        var source = new ContainerFileSource(client, "abc", "/", canWrite: true,
            listFolder: (_, _) => { shellTries++; return Task.FromResult<IReadOnlyList<FileEntry>?>(null); });
        var status = new Progress<string>();

        Assert.Equal(["bin", "etc"], (await source.ListAsync("/", status, CancellationToken.None)).Select(e => e.Name));
        Assert.Equal(["apt", "hosts"], (await source.ListAsync("/etc", status, CancellationToken.None)).Select(e => e.Name));
        Assert.Equal(1, handler.Archives);
        Assert.Equal(1, shellTries);

        source.Forget();
        await source.ListAsync("/etc/apt", status, CancellationToken.None);
        Assert.Equal(2, handler.Archives);
        Assert.Equal(2, shellTries);
    }
}
