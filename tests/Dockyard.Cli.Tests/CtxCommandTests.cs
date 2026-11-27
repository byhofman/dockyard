namespace Dockyard.Cli.Tests;

public sealed class CtxCommandTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dockyard-ctx-cli-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string Kubeconfig()
    {
        var path = Path.Combine(dir, "config");
        File.WriteAllText(path, "apiVersion: v1\nkind: Config\ncurrent-context: prod\n"
            + "clusters:\n- name: dev\n  cluster:\n    server: https://dev\n- name: prod\n  cluster:\n    server: https://prod\n"
            + "contexts:\n- name: dev\n  context:\n    cluster: dev\n    user: u\n- name: prod\n  context:\n    cluster: prod\n    user: u\n"
            + "users:\n- name: u\n  user:\n    token: t\n");
        return path;
    }

    [Fact]
    public void A_context_opens_a_shell_on_a_file_of_only_that_context_and_deletes_it_afterwards()
    {
        using var h = new CliHarness(kubeconfig: Kubeconfig());
        h.Shell.Behaviour = path => { h.Shell.Seen = File.ReadAllText(path); return 0; };

        Assert.Equal(0, h.Run("ctx", "dev"));

        Assert.Equal("dev", h.Shell.Context);
        Assert.Contains("https://dev", h.Shell.Seen);
        Assert.DoesNotContain("https://prod", h.Shell.Seen);
        Assert.False(File.Exists(h.Shell.Path));
    }

    [Fact]
    public void The_file_is_deleted_also_when_the_shell_exits_non_zero_or_fails()
    {
        using var h = new CliHarness(kubeconfig: Kubeconfig());
        h.Shell.Behaviour = _ => 130;

        Assert.Equal(0, h.Run("ctx", "dev"));
        Assert.False(File.Exists(h.Shell.Path));

        h.Shell.Behaviour = _ => throw new InvalidOperationException("no shell");

        Assert.Equal(1, h.Run("ctx", "dev"));
        Assert.False(File.Exists(h.Shell.Path));
    }

    [Fact]
    public void An_unknown_context_exits_2_and_starts_no_shell()
    {
        using var h = new CliHarness(kubeconfig: Kubeconfig());

        Assert.Equal(2, h.Run("ctx", "nope"));

        Assert.Contains("nope", h.Error);
        Assert.Null(h.Shell.Path);
    }

    [Fact]
    public void Without_a_context_the_prompt_lists_the_current_one_first()
    {
        using var h = new CliHarness(kubeconfig: Kubeconfig());
        h.Keys.Press(ConsoleKey.Enter, '\r');

        Assert.Equal(0, h.Run("ctx"));

        Assert.Equal("prod", h.Shell.Context);
    }

    [Fact]
    public void Without_a_terminal_the_contexts_are_printed_and_it_exits_2()
    {
        using var h = new CliHarness(kubeconfig: Kubeconfig()) { HasTerminal = false };

        Assert.Equal(2, h.Run("ctx"));

        Assert.Equal(["prod", "dev"], h.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        Assert.Null(h.Shell.Path);
    }
}
