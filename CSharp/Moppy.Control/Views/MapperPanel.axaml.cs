using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Moppy.Core.Control;

namespace Moppy.Control.Views;

/// <summary>
/// Edits one <see cref="ConfiguredMapper"/>: for each of its four scripts, a preset choice and the script text
/// (editable when "Custom" is chosen; shown in red if invalid).
/// </summary>
public partial class MapperPanel : UserControl
{
    private static readonly (MapperField Field, string Label)[] Fields =
    [
        (MapperField.Condition, "Condition"),
        (MapperField.DeviceAddress, "Device Address"),
        (MapperField.SubAddress, "Sub Address"),
        (MapperField.Note, "Note"),
    ];

    private static readonly IBrush ReadOnlyBackground = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));

    private readonly Dictionary<MapperField, (ComboBox Choice, TextBox Script)> editors = new();
    private bool editingEnabled = true;

    // For the XAML designer
    public MapperPanel()
        : this(new ConfiguredMapper(new MidiScriptMapperConfig()), () => { })
    {
    }

    public MapperPanel(ConfiguredMapper mapper, Action remove)
    {
        Mapper = mapper;
        InitializeComponent();
        DeleteMapperButton.Click += (_, _) => remove();

        var grid = (Grid)((Border)Content!).Child!;
        for (int i = 0; i < Fields.Length; i++)
        {
            var (field, label) = Fields[i];
            var choice = new ComboBox
            {
                ItemsSource = MapperPresets.ChoicesFor(field),
                SelectedItem = mapper.GetChoice(field),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var script = new TextBox { Classes = { "script" } };

            var header = new DockPanel { Margin = new Avalonia.Thickness(0, 0, 0, 4) };
            var labelBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(labelBlock, Dock.Left);
            header.Children.Add(labelBlock);
            header.Children.Add(choice);

            Grid.SetRow(header, 1);
            Grid.SetColumn(header, i * 2);
            Grid.SetRow(script, 2);
            Grid.SetColumn(script, i * 2);
            grid.Children.Add(header);
            grid.Children.Add(script);
            editors[field] = (choice, script);

            choice.SelectionChanged += (_, _) =>
            {
                if (choice.SelectedItem is string selected && selected != Mapper.GetChoice(field))
                {
                    Mapper.SelectChoice(field, selected);
                    ShowScript(field);
                }
            };
            script.LostFocus += (_, _) =>
            {
                if (Mapper.IsCustom(field) && script.Text is { } text)
                {
                    Mapper.SetCustomScript(field, text);
                    ShowError(field);
                }
            };

            ShowScript(field);
        }
    }

    public ConfiguredMapper Mapper { get; }

    public void EnableEditing(bool enable)
    {
        editingEnabled = enable;
        foreach (var (field, (choice, script)) in editors)
        {
            choice.IsEnabled = enable;
            script.IsReadOnly = !enable || !Mapper.IsCustom(field);
        }

        DeleteMapperButton.IsEnabled = enable;
    }

    private void ShowScript(MapperField field)
    {
        var (_, script) = editors[field];
        bool custom = Mapper.IsCustom(field);
        script.Text = Mapper.GetChoiceScript(field);
        script.IsReadOnly = !custom || !editingEnabled;
        script.Background = custom ? null : ReadOnlyBackground;
        ShowError(field);
    }

    private void ShowError(MapperField field)
    {
        var (_, script) = editors[field];
        string? error = Mapper.GetError(field);
        script.Classes.Set("invalid", error is not null);
        ToolTip.SetTip(script, error);
    }
}
