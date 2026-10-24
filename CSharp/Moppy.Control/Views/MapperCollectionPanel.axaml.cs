using Avalonia.Controls;
using Avalonia.Threading;
using Moppy.Core.Control;
using Moppy.Core.Status;

namespace Moppy.Control.Views;

/// <summary>
/// Shows a <see cref="MapperPanel"/> for each of the controller's mappers.  Editing is disabled while playing.
/// </summary>
public partial class MapperCollectionPanel : UserControl, IStatusConsumer
{
    private MoppyController? controller;
    private bool editingEnabled = true;

    public MapperCollectionPanel()
    {
        InitializeComponent();
    }

    private MoppyController Controller => controller ?? throw new InvalidOperationException("Not initialized");

    public void Initialize(MoppyController moppyController)
    {
        controller = moppyController;
        AddMapperButton.Click += (_, _) => Controller.AddMapper();
        controller.MappersChanged += (_, _) => Dispatcher.UIThread.Post(RefreshMappers);
        controller.StatusBus.RegisterConsumer(this);
        RefreshMappers();
    }

    public void ReceiveUpdate(StatusUpdate update)
    {
        switch (update.Type)
        {
            case StatusType.SequenceStart:
                Dispatcher.UIThread.Post(() => EnableMapperEditing(false));
                break;
            case StatusType.SequenceStopped:
            case StatusType.SequenceEnd:
            case StatusType.SequencePause:
                Dispatcher.UIThread.Post(() => EnableMapperEditing(true));
                break;
        }
    }

    private void RefreshMappers()
    {
        var existing = MappersPanel.Children.OfType<MapperPanel>().ToDictionary(p => p.Mapper);
        MappersPanel.Children.Clear();
        foreach (var mapper in Controller.ConfiguredMappers)
        {
            var panel = existing.GetValueOrDefault(mapper) ?? new MapperPanel(mapper, () => Controller.RemoveMapper(mapper));
            panel.EnableEditing(editingEnabled);
            MappersPanel.Children.Add(panel);
        }
    }

    private void EnableMapperEditing(bool enable)
    {
        editingEnabled = enable;
        AddMapperButton.IsEnabled = enable;
        foreach (var panel in MappersPanel.Children.OfType<MapperPanel>())
        {
            panel.EnableEditing(enable);
        }
    }
}
