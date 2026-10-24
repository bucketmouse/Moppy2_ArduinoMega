using Avalonia.Controls;
using Moppy.Core.Control;

namespace Moppy.Control;

public partial class MainWindow : Window
{
    private readonly MoppyController controller;
    private bool settingsSaved;

    // For the XAML designer
    public MainWindow()
        : this(new MoppyController(MoppyPreferences.InMemory(), new NetworkManager(new Core.Status.StatusBus(), false, () => [])))
    {
    }

    public MainWindow(MoppyController controller)
    {
        this.controller = controller;
        InitializeComponent();

        var config = controller.Config;
        Width = Math.Max(MinWidth, config.MainWindowSize.Width);
        Height = Math.Max(MinHeight, config.MainWindowSize.Height);
        SplitGrid.RowDefinitions[0].Height = new GridLength(Math.Max(150, config.MainWindowDividerPosition));

        SequencerPanel.Initialize(controller);
        NetworkPanel.Initialize(controller);
        MapperCollectionPanel.Initialize(controller);

        Closing += (_, _) => SaveSettings();
    }

    /// <summary>Saves window layout and mappers to the configuration file.</summary>
    public void SaveSettings()
    {
        if (settingsSaved)
        {
            return;
        }

        settingsSaved = true;
        if (WindowState == WindowState.Normal)
        {
            controller.Config.MainWindowSize = new WindowSize((int)Width, (int)Height);
        }

        controller.Config.MainWindowDividerPosition = (int)SplitGrid.RowDefinitions[0].ActualHeight;

        // Save our mappers (and the rest of the config) before closing
        controller.SaveMappersToConfig();
    }
}
