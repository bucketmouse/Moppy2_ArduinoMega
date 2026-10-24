using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using Moppy.Core;
using Moppy.Core.Control;

namespace Moppy.Control;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (configPath, files) = ParseArguments(desktop.Args ?? []);
            configPath ??= MoppyPreferences.DefaultConfigPath;

            //
            //// Initialize logger
            //
            var loggerFactory = LoggerFactory.Create(builder => builder
                .SetMinimumLevel(LogLevel.Information)
                .AddProvider(new FileLoggerProvider(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "moppy_control.log"))));
            MoppyLogging.LoggerFactory = loggerFactory;

            //
            //// Initialize Moppy system components
            //
            var controller = new MoppyController(MoppyPreferences.Load(configPath));
            controller.Start();
            controller.Playlist.AddRange(files.Where(File.Exists).Select(Path.GetFullPath));

            var mainWindow = new MainWindow(controller);
            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += (_, _) => mainWindow.SaveSettings();
            desktop.Exit += (_, _) =>
            {
                // Close everything down properly (stops playback and sends a stop to every connected network)
                controller.Dispose();
                loggerFactory.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static (string? ConfigPath, List<string> Files) ParseArguments(string[] args)
    {
        string? configPath = null;
        var files = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length)
            {
                configPath = args[++i];
            }
            else
            {
                files.Add(args[i]);
            }
        }

        return (configPath, files);
    }
}
