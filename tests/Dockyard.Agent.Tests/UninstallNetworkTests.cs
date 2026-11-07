using System.Diagnostics;

namespace Dockyard.Agent.Tests;

/// <summary>DOK-19: uninstall on a Linux host takes away every rule and address the agent added, without a reboot. The
/// setup script matches them by what is Dockyard's own, so this runs it against the agent's own list.</summary>
public sealed class UninstallNetworkTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "dockyard-uninstall-" + Guid.NewGuid().ToString("N"));

    public UninstallNetworkTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    static string Script()
    {
        var at = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(at, "Dockyard.slnx"))) at = Path.GetDirectoryName(at)!;
        return Path.Combine(at, "src", "Dockyard", "Hosts", "setup-linux.sh");
    }

    // How `iptables -S` prints a rule the agent inserted: appended, and a single address with its prefix.
    static string Listed(string[] command)
    {
        var args = command.SkipWhile(a => a != "-I").Skip(1).ToList();
        for (var i = 1; i < args.Count; i++)
            if (args[i - 1] == "-d" && !args[i].Contains('/')) args[i] += "/32";
        return "-A " + string.Join(' ', args);
    }

    static string Table(string[] command) => command.Length > 2 && command[1] == "-t" ? command[2] : "filter";

    // Fakes for iptables and ip: they print the listing they were given and write down every call.
    void Fake(string name, string body) =>
        File.WriteAllText(Path.Combine(dir, name), "#!/bin/sh\necho \"" + name + " $*\" >> \"$CALLS\"\n" + body);

    async Task<List<string>> Uninstall(Dictionary<string, List<string>> listings)
    {
        foreach (var (table, lines) in listings)
            File.WriteAllLines(Path.Combine(dir, table + ".rules"), lines);
        Fake("iptables", "t=filter; prev=\"\"\nfor a in \"$@\"; do [ \"$prev\" = -t ] && t=\"$a\"; prev=\"$a\"; done\ncase \" $* \" in *\" -S \"*) cat \"$RULES/$t.rules\" 2>/dev/null ;; esac\nexit 0\n");
        Fake("ip", "exit 0\n");
        foreach (var name in new[] { "iptables", "ip" })
            File.SetUnixFileMode(Path.Combine(dir, name), (UnixFileMode)0b111_101_101);

        var calls = Path.Combine(dir, "calls");
        var start = new ProcessStartInfo("sh", [Script(), "remove-network"]) { RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["PATH"] = dir + ":" + Environment.GetEnvironmentVariable("PATH");
        start.Environment["CALLS"] = calls;
        start.Environment["RULES"] = dir;
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return File.Exists(calls) ? [.. File.ReadAllLines(calls)] : [];
    }

    [Fact]
    public async Task Every_rule_and_address_the_agent_adds_on_a_host_is_removed_and_nothing_else()
    {
        if (OperatingSystem.IsWindows()) return;
        var added = NetworkRules.Commands().Concat(NetworkRules.HostCommands()).ToList();
        var listings = new Dictionary<string, List<string>>
        {
            ["filter"] = ["-P INPUT ACCEPT", "-N DOCKYARD-INPUT", "-A DOCKYARD-INPUT -i lo -j ACCEPT", "-A DOCKYARD-INPUT -j DROP", "-A INPUT -i eth0 -p tcp --dport 22 -j ACCEPT", "-A DOCKER-USER -j RETURN"],
            ["nat"] = ["-P OUTPUT ACCEPT", "-A POSTROUTING -s 172.17.0.0/16 ! -o docker0 -j MASQUERADE", "-A DOCKER -i docker0 -j RETURN"],
        };
        foreach (var command in added)
            listings[Table(command)].Add(Listed(command));

        var calls = await Uninstall(listings);

        foreach (var command in added)
        {
            var table = Table(command);
            var expected = $"iptables -w -t {table} -D {Listed(command)[3..]}";
            Assert.Contains(expected, calls);
        }
        Assert.Equal(added.Count, calls.Count(c => c.Contains(" -D ")));
        Assert.Contains("iptables -w -t filter -F DOCKYARD-INPUT", calls);
        Assert.Contains("iptables -w -t filter -X DOCKYARD-INPUT", calls);
        Assert.Contains($"ip addr del {GuestAddresses.Dns}/32 dev lo", calls);
        Assert.Contains($"ip addr del {GuestAddresses.Host}/32 dev lo", calls);
    }

    [Fact]
    public async Task A_rule_left_twice_by_an_older_agent_is_removed_twice()
    {
        if (OperatingSystem.IsWindows()) return;
        var rule = Listed(NetworkRules.Commands().First(c => c.Contains("OUTPUT")));

        var calls = await Uninstall(new() { ["filter"] = [], ["nat"] = [rule, rule] });

        Assert.Equal(2, calls.Count(c => c == $"iptables -w -t nat -D {rule[3..]}"));
    }
}
