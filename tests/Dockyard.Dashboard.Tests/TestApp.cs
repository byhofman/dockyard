using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Dockyard.Dashboard.Tests.TestApp))]

namespace Dockyard.Dashboard.Tests;

static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
