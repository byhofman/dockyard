using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Tests;

public sealed class DialogFocusTests
{
    [Fact]
    public async Task Tab_and_shift_tab_stay_inside_an_open_dialog()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DialogFocusTests).Assembly);
        var outside = await session.Dispatch(() =>
        {
            var vm = new ShellViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                vm.Open(new Dialog("Pull an image", "", "Pull", _ => Task.CompletedTask, fields: [new DialogField("Image"), new DialogField("Tag")]));
                Dispatcher.UIThread.RunJobs();
                var layer = window.FindControl<Panel>("DialogLayer")!;
                var escaped = new List<string>();
                foreach (var shift in new[] { false, false, false, false, false, true, true, true, true, true })
                {
                    window.KeyPress(Key.Tab, shift ? RawInputModifiers.Shift : RawInputModifiers.None, PhysicalKey.Tab, null);
                    Dispatcher.UIThread.RunJobs();
                    if (window.FocusManager?.GetFocusedElement() is not Visual focused || !layer.IsVisualAncestorOf(focused))
                        escaped.Add(window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing");
                }
                return escaped;
            }
            finally { window.Close(); }
        }, CancellationToken.None);

        Assert.Empty(outside);
    }
}
