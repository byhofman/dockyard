using System.Runtime.CompilerServices;
using Xunit.v3;

namespace Dockyard.InstallTests;

[Flags]
public enum InstallOs { None = 0, Windows = 1, MacOS = 2, Linux = 4 }

public static class Gate
{
    public static bool Flag(string name) => Environment.GetEnvironmentVariable(name) == "1";

    public static InstallOs Current =>
        OperatingSystem.IsWindows() ? InstallOs.Windows : OperatingSystem.IsMacOS() ? InstallOs.MacOS : OperatingSystem.IsLinux() ? InstallOs.Linux : InstallOs.None;

    public static bool On(InstallOs declared) => SkipReason(declared, Current, Flag("DOCKYARD_INSTALL_TESTS")) is null;

    public static string? SkipReason(InstallOs declared, InstallOs current, bool enabled) =>
        !enabled ? "Install tests install Dockyard for real; set DOCKYARD_INSTALL_TESTS=1"
        : !declared.HasFlag(current) || current == InstallOs.None ? $"Runs on {declared} only"
        : null;
}

// Every install test carries this instead of FactAttribute: it adds the Install category and holds both guards, so a test
// cannot forget either.
[AttributeUsage(AttributeTargets.Method)]
public sealed class InstallFactAttribute : FactAttribute, ITraitAttribute
{
    public InstallFactAttribute(InstallOs os, [CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) => Skip = Gate.SkipReason(os, Gate.Current, Gate.Flag("DOCKYARD_INSTALL_TESTS"));

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new("Category", "Install")];
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class InstallTheoryAttribute : TheoryAttribute, ITraitAttribute
{
    public InstallTheoryAttribute(InstallOs os, [CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) => Skip = Gate.SkipReason(os, Gate.Current, Gate.Flag("DOCKYARD_INSTALL_TESTS"));

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new("Category", "Install")];
}

public class GateTests
{
    [Theory]
    [InlineData(InstallOs.Windows, InstallOs.Windows, true, null)]
    [InlineData(InstallOs.Windows, InstallOs.Linux, true, "Runs on Windows only")]
    [InlineData(InstallOs.MacOS | InstallOs.Linux, InstallOs.Linux, true, null)]
    [InlineData(InstallOs.MacOS | InstallOs.Linux, InstallOs.MacOS, true, null)]
    [InlineData(InstallOs.MacOS | InstallOs.Linux, InstallOs.Windows, true, "Runs on MacOS, Linux only")]
    [InlineData(InstallOs.Windows, InstallOs.Windows, false, "set DOCKYARD_INSTALL_TESTS=1")]
    [InlineData(InstallOs.Linux, InstallOs.MacOS, false, "set DOCKYARD_INSTALL_TESTS=1")]
    [InlineData(InstallOs.Linux, InstallOs.None, true, "Runs on Linux only")]
    public void The_skip_decision_follows_the_env_var_and_the_declared_systems(InstallOs declared, InstallOs current, bool enabled, string? reason)
    {
        var skip = Gate.SkipReason(declared, current, enabled);
        if (reason is null) Assert.Null(skip); else Assert.Contains(reason, skip);
    }

    [Fact]
    public void An_install_fact_is_in_the_Install_category_and_skips_here_without_the_env_var()
    {
        var attribute = new InstallFactAttribute(InstallOs.Windows | InstallOs.MacOS | InstallOs.Linux);
        Assert.Contains(new KeyValuePair<string, string>("Category", "Install"), attribute.GetTraits());
        Assert.Equal(Gate.Flag("DOCKYARD_INSTALL_TESTS"), attribute.Skip is null);
    }
}
