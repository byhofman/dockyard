using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dockyard.Tray.Tests;

// The macOS bundle and the dockyard Linux package ship only the tray's native libraries, and the dashboard loads those, so
// both have to be built against the same Avalonia, SkiaSharp and HarfBuzzSharp.
public sealed partial class NativeLibraryVersionTests
{
    static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Dockyard.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    static Dictionary<string, string> References(string project) =>
        PackageReference().Matches(File.ReadAllText(Path.Combine(Root(), "src", project, project + ".csproj")))
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["version"].Value);

    // What restore resolved, transitive packages included: SkiaSharp and HarfBuzzSharp come through Avalonia.
    static Dictionary<string, string> Resolved(string project)
    {
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "src", project, "obj", "project.assets.json")));
        return assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(p => p.Name.Split('/'))
            .Where(p => p[0].StartsWith("Avalonia", StringComparison.Ordinal) || p[0].StartsWith("SkiaSharp", StringComparison.Ordinal) || p[0].StartsWith("HarfBuzzSharp", StringComparison.Ordinal))
            .ToDictionary(p => p[0], p => p[1]);
    }

    [Fact]
    public void The_tray_and_the_dashboard_reference_the_same_Avalonia()
    {
        var tray = References("Dockyard.Tray");
        var dashboard = References("Dockyard.Dashboard");
        Assert.Contains("Avalonia", tray.Keys);
        foreach (var (name, version) in tray.Where(r => r.Key.StartsWith("Avalonia", StringComparison.Ordinal)))
            Assert.Equal((name, version), (name, dashboard.GetValueOrDefault(name)));
    }

    [Fact]
    public void The_dashboard_needs_no_native_library_the_tray_does_not_bring_in_the_same_version()
    {
        var tray = Resolved("Dockyard.Tray");
        var dashboard = Resolved("Dockyard.Dashboard");
        Assert.Contains("SkiaSharp", tray.Keys);
        Assert.Contains("HarfBuzzSharp", tray.Keys);
        foreach (var (name, version) in dashboard.Where(r => r.Key.StartsWith("SkiaSharp", StringComparison.Ordinal) || r.Key.StartsWith("HarfBuzzSharp", StringComparison.Ordinal) || r.Key == "Avalonia.Native" || r.Key == "Avalonia.Skia"))
            Assert.Equal((name, version), (name, tray.GetValueOrDefault(name)));
    }

    [GeneratedRegex("""<PackageReference Include="(?<name>[^"]+)" Version="(?<version>[^"]+)" """)]
    private static partial Regex PackageReference();
}
