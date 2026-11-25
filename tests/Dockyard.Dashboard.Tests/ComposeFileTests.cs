using System.Text;
using Dockyard.Dashboard.Features.Resources;

namespace Dockyard.Dashboard.Tests;

public sealed class ComposeFileTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("compose-file-").FullName;

    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void Saving_keeps_the_line_endings_and_byte_order_mark_the_file_had()
    {
        var path = Path.Combine(dir, "compose.yaml");
        File.WriteAllBytes(path, [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("services:\r\n  web:\r\n    image: nginx\r\n")]);

        var file = ComposeFile.Load(path);
        Assert.Equal("services:\n  web:\n    image: nginx\n", file.Text);
        Assert.False(file.IsChangedBy("services:\r\n  web:\r\n    image: nginx\r\n"));

        file.Save("services:\n  web:\n    image: caddy\n");

        Assert.Equal([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("services:\r\n  web:\r\n    image: caddy\r\n")], File.ReadAllBytes(path));
        Assert.False(file.IsChangedBy("services:\n  web:\n    image: caddy\n"));
    }

    [Fact]
    public void A_file_with_plain_newlines_stays_that_way()
    {
        var path = Path.Combine(dir, "compose.yaml");
        File.WriteAllText(path, "services: {}\n", new UTF8Encoding(false));

        ComposeFile.Load(path).Save("services:\n  db:\n    image: postgres\n");

        Assert.Equal("services:\n  db:\n    image: postgres\n"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void Broken_yaml_is_reported_on_its_line()
    {
        Assert.Null(ComposeFile.Problem("services:\n  web:\n    image: nginx\n"));
        Assert.Equal(3, ComposeFile.Problem("services:\n  web:\n    image: [nginx\n")?.Line ?? 0, new LineAround());
        Assert.Equal(1, ComposeFile.Problem("- just\n- a list\n")?.Line);
    }

    [Fact]
    public void The_check_feeds_the_edited_file_in_place_of_its_own()
    {
        var project = new ComposeProject("shop", @"C:\src\shop", [@"C:\src\shop\compose.yaml", @"C:\src\shop\compose.override.yaml"], []);

        Assert.Equal(
            ["--context", "default", "compose", "-p", "shop", "-f", "-", "-f", @"C:\src\shop\compose.override.yaml", "--project-directory", @"C:\src\shop", "config", "-q"],
            ComposeFile.ConfigArgs("default", project, @"C:\src\shop\compose.yaml"));
        Assert.Equal(7, ComposeFile.LineOf("yaml: line 7: did not find expected key"));
    }

    sealed class LineAround : IEqualityComparer<int>
    {
        public bool Equals(int expected, int actual) => Math.Abs(expected - actual) <= 1;
        public int GetHashCode(int value) => 0;
    }
}
