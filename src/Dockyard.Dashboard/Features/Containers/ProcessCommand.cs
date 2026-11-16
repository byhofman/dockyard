using System.Diagnostics;

namespace Dockyard.Dashboard.Features.Containers;

sealed record ProcessCommand(string File, IReadOnlyList<string> Args)
{
    public void Start()
    {
        var psi = new ProcessStartInfo(File) { UseShellExecute = false, CreateNoWindow = false };
        foreach (var a in Args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }
}
