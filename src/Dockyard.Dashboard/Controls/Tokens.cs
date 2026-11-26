using Avalonia;
using Avalonia.Controls;

namespace Dockyard.Dashboard.Controls;

static class Tokens
{
    // A theme token on a control built in code, which follows the theme as DynamicResource does in XAML.
    public static T Token<T>(this T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }
}
