using Avalonia;
using Avalonia.Headless;
using Moppy.Control;

[assembly: AvaloniaTestApplication(typeof(Moppy.Control.Tests.TestAppBuilder))]

namespace Moppy.Control.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
