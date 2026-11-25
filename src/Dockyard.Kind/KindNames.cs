using System.Text.RegularExpressions;

namespace Dockyard.Kind;

static partial class KindNames
{
    public static bool IsValid(string name) => ValidName().IsMatch(name);

    [GeneratedRegex(@"^[a-z0-9.-]+$")]
    private static partial Regex ValidName();
}
