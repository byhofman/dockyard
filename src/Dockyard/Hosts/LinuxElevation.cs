namespace Dockyard.Hosts;

// How the steps that need root get it, in order of least interruption. A silent setup without a terminal or a
// desktop has no one to ask: then there is no way, and setup says so instead of hanging on a prompt.
static class LinuxElevation
{
    public static string[]? Prefix(bool isRoot, bool passwordlessSudo, bool hasDisplay, bool hasPkexec, bool hasSudo, bool hasTerminal)
    {
        if (isRoot) return [];
        if (passwordlessSudo) return ["sudo", "-n"];
        if (hasDisplay && hasPkexec) return ["pkexec"];
        if (hasTerminal && hasSudo) return ["sudo"];
        if (hasTerminal && hasPkexec) return ["pkexec"];
        return null;
    }
}
