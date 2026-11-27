using Dockyard.Setup;

namespace Dockyard.Setup.Tests;

public sealed class ContextKubeconfigTests : IDisposable
{
    readonly TempSettings settings = new();
    readonly string dir = Directory.CreateTempSubdirectory("dockyard-ctxfile-").FullName;
    readonly List<string> written = [];

    public void Dispose()
    {
        foreach (var file in written) File.Delete(file);
        settings.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    string Fixture()
    {
        File.WriteAllText(Path.Combine(dir, "ca.crt"), "the-ca");
        File.WriteAllText(Path.Combine(dir, "client.crt"), "the-cert");
        File.WriteAllText(Path.Combine(dir, "client.key"), "the-key");
        var path = Path.Combine(dir, "config");
        File.WriteAllText(path, """
            apiVersion: v1
            kind: Config
            current-context: other
            clusters:
            - name: dev
              cluster:
                server: https://dev.example
                certificate-authority: ca.crt
            - name: other
              cluster:
                server: https://other.example
            users:
            - name: dev-user
              user:
                client-certificate: client.crt
                client-key: client.key
            - name: other-user
              user:
                token: secret-other
            contexts:
            - name: dev
              context:
                cluster: dev
                user: dev-user
                namespace: shop
            - name: other
              context:
                cluster: other
                user: other-user
            """);
        return path;
    }

    string Write(string context)
    {
        var path = Facade.For(new FakeHost(), settings, [Fixture()]).WriteContextKubeconfig(context);
        written.Add(path);
        return path;
    }

    static string B64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Only_the_asked_context_with_its_cluster_and_user_is_in_the_file()
    {
        var text = File.ReadAllText(Write("dev"));

        Assert.Contains("current-context: dev", text);
        Assert.Contains("https://dev.example", text);
        Assert.Contains("namespace: shop", text);
        Assert.DoesNotContain("other", text);
        Assert.DoesNotContain("secret-other", text);
    }

    [Fact]
    public void Certificates_are_inlined_in_place_of_their_paths()
    {
        var text = File.ReadAllText(Write("dev"));

        Assert.Contains($"certificate-authority-data: {B64("the-ca")}", text);
        Assert.Contains($"client-certificate-data: {B64("the-cert")}", text);
        Assert.Contains($"client-key-data: {B64("the-key")}", text);
        Assert.DoesNotContain("ca.crt", text);
        Assert.DoesNotContain("client.key\n", text);
    }

    [Fact]
    public void The_file_is_readable_by_its_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Write("dev")));
    }

    [Fact]
    public void An_unknown_context_is_refused_and_nothing_is_written()
    {
        var setup = Facade.For(new FakeHost(), settings, [Fixture()]);

        var ex = Assert.Throws<ArgumentException>(() => setup.WriteContextKubeconfig("nope"));

        Assert.Contains("There is no context named nope", ex.Message);
    }
}
