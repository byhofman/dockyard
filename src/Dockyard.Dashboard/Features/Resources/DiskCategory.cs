using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Features.Resources;

sealed record DiskCategory(string Name, long Total, long Reclaimable, int Count)
{
    public string TotalText => Format.Bytes(Total);
    public string ReclaimableText => Format.Bytes(Reclaimable);
    public double Fraction => Total > 0 ? (double)Reclaimable / Total : 0;
}
