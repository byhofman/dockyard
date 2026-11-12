namespace Dockyard.Agent;

// vsock ports the helper on the Mac connects to.
static class Ports
{
    public const int Docker = 2375;
    public const int Forward = 2376;
    public const int Control = 2377;
    public const int Api = 2379;
}
