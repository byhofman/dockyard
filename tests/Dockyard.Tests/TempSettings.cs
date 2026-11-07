using Dockyard.Core;

namespace Dockyard.Tests;

/// <summary>Settings in a file of the test's own, so no test reads or writes the user's real
/// settings or another test's.</summary>
sealed class TempSettings : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "dockyard-tests-" + Guid.NewGuid().ToString("N"));

    public string File => Path.Combine(dir, "settings.json");
    public Settings Settings { get; }

    public TempSettings() => Settings = Settings.Load(File);

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
