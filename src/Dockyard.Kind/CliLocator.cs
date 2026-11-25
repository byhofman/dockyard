namespace Dockyard.Kind;

// Where Dockyard or a package manager puts a command. An app started from the menu bar, at login, or before winget
// changed the PATH does not have these on its PATH, so they are looked at first, Dockyard's own copy before the rest.
static class CliLocator
{
    public static IEnumerable<string> KnownDirs(string home)
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return
            [
                Path.Combine(programFiles, "Docker"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
                Path.Combine(programFiles, "WinGet", "Links"),
            ];
        }
        if (OperatingSystem.IsMacOS()) return [Path.Combine(home, ".dockyard", "bin"), "/opt/homebrew/bin", "/usr/local/bin"];
        return ["/usr/local/bin", "/usr/bin"];
    }

    // wingetPackages: where winget could not make a link, it put the package's folder on the PATH, which an app started
    // earlier lacks.
    public static string? Find(string command, Func<string, bool> exists, string? path, string home, string? wingetPackages = null)
    {
        var file = OperatingSystem.IsWindows() ? command + ".exe" : command;
        var onPath = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return KnownDirs(home).Concat(onPath).Select(d => Path.Combine(d, file)).FirstOrDefault(exists)
            ?? (wingetPackages is null ? null : InWingetPackage(wingetPackages, file));
    }

    public static string WingetPackages => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");

    internal static string? InWingetPackage(string packages, string file)
    {
        if (!Directory.Exists(packages)) return null;
        try
        {
            return Directory.EnumerateDirectories(packages)
                .SelectMany(d => Directory.EnumerateFiles(d, file, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }))
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string? Locate(string command) =>
        Find(command, File.Exists, Environment.GetEnvironmentVariable("PATH"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows() ? WingetPackages : null);
}
