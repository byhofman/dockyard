namespace Dockyard.Core;

// A running exe on Windows cannot be overwritten or deleted, only renamed. A "docker logs -f" in some terminal must
// not make an update fail, so what is in use moves aside and goes at the next install.
static class InUseFiles
{
    const string Suffix = ".old";

    public static void MoveAside(string target)
    {
        if (!File.Exists(target)) return;
        try
        {
            File.Delete(target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Move(target, $"{target}.{Guid.NewGuid():N}{Suffix}");
        }
    }

    public static void CleanUp(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*" + Suffix, SearchOption.AllDirectories))
        {
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
