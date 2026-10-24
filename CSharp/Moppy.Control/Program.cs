using Avalonia;

namespace Moppy.Control;

internal static class Program
{
    /// <summary>
    /// Usage: MoppyControl [--config &lt;path to config json&gt;] [MIDI files to add to the playlist...]
    /// </summary>
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
