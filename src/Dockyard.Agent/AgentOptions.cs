namespace Dockyard.Agent;

sealed record AgentOptions(AgentMode Mode)
{
    // dockyard.agent=tcp on the kernel command line serves the same ports over TCP (used by the QEMU boot test).
    public static AgentOptions From(IReadOnlyList<string> args, string cmdline)
    {
        if (args.Contains("--mode=wsl")) return new(AgentMode.Wsl);
        if (args.Contains("--mode=host")) return new(AgentMode.Host);
        return new(cmdline.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Contains("dockyard.agent=tcp") ? AgentMode.TcpTest : AgentMode.Vm);
    }
}
