namespace Dockyard.Core;

// Whether a person can be asked here, for setup's choices and for sudo's password alike: both read the terminal that the
// input and the output are. The one-liners hand setup the terminal when they are piped into sh.
static class Terminal
{
    public static bool IsAttached => !Console.IsInputRedirected && !Console.IsOutputRedirected;
}
