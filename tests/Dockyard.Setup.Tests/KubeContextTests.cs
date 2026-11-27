using Dockyard.Setup;

namespace Dockyard.Setup.Tests;

public sealed class KubeContextTests : IDisposable
{
    readonly TempSettings settings = new();
    readonly string dir = Directory.CreateTempSubdirectory("dockyard-kubecontext-").FullName;

    public void Dispose()
    {
        settings.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    string Write(string name, string? current, params string[] contexts)
    {
        var path = Path.Combine(dir, name);
        var text = "apiVersion: v1\nkind: Config\npreferences:\n  colors: true\n"
            + (current is null ? "" : $"current-context: {current}\n")
            + "clusters:\n" + string.Concat(contexts.Select(c => $"- name: {c}\n  cluster:\n    server: https://{c}.example\n"))
            + "contexts:\n" + string.Concat(contexts.Select(c => $"- name: {c}\n  context:\n    cluster: {c}\n    user: {c}\n"));
        File.WriteAllText(path, text);
        return path;
    }

    DockyardSetup Setup(params string[] files) => Facade.For(new FakeHost(), settings, files);

    [Fact]
    public void Lists_the_contexts_of_every_file_and_the_current_one()
    {
        var a = Write("a", null, "dev", "prod");
        var b = Write("b", "stage", "stage", "dev");

        var found = Setup(a, b).KubeContexts();

        Assert.Equal(["dev", "prod", "stage"], found.Names);
        Assert.Equal("stage", found.Current);
    }

    [Fact]
    public void Without_a_kubeconfig_there_are_no_contexts()
    {
        var found = Setup(Path.Combine(dir, "missing")).KubeContexts();

        Assert.Empty(found.Names);
        Assert.Null(found.Current);
    }

    [Fact]
    public void Switching_writes_only_the_current_context_into_the_file_that_has_one()
    {
        var a = Write("a", null, "dev");
        var b = Write("b", "stage", "stage", "prod");
        var before = File.ReadAllText(a);

        Setup(a, b).UseKubeContext("dev");

        Assert.Equal(before, File.ReadAllText(a));
        Assert.Equal("dev", Setup(a, b).KubeContexts().Current);
        var changed = File.ReadAllText(b);
        Assert.Contains("current-context: dev", changed);
        Assert.Contains("colors: true", changed);
        Assert.Contains("server: https://prod.example", changed);
        Assert.Contains("name: stage", changed);
    }

    [Fact]
    public void Switching_with_no_current_context_anywhere_writes_the_first_existing_file()
    {
        var a = Write("a", null, "dev", "prod");

        Setup(Path.Combine(dir, "missing"), a).UseKubeContext("prod");

        Assert.Equal("prod", Setup(a).KubeContexts().Current);
    }

    [Fact]
    public void An_unknown_context_is_refused_and_nothing_changes()
    {
        var a = Write("a", "dev", "dev");
        var before = File.ReadAllText(a);

        Assert.Throws<ArgumentException>(() => Setup(a).UseKubeContext("nope"));

        Assert.Equal(before, File.ReadAllText(a));
    }
}
