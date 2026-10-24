using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Moppy.Core;
using Moppy.Core.Comms.Bridge;
using Moppy.Core.Control;
using Moppy.Core.Device;
using Moppy.Core.Status;

namespace Moppy.Control.Views;

/// <summary>
/// Lists network bridges (with connect checkboxes and connection options) and the devices discovered on them.
/// </summary>
public partial class NetworkPanel : UserControl, IStatusConsumer
{
    private NetworkManager? netManager;

    public NetworkPanel()
    {
        InitializeComponent();
    }

    private NetworkManager NetManager => netManager ?? throw new InvalidOperationException("Not initialized");

    private static ILogger Log => MoppyLogging.LoggerFactory.CreateLogger<NetworkPanel>();

    public void Initialize(MoppyController controller)
    {
        netManager = controller.NetworkManager;
        RefreshButton.Click += (_, _) => NetManager.RefreshAvailableBridges();
        controller.StatusBus.RegisterConsumer(this);
        UpdateBridgesList();
        UpdateDevicesList();
    }

    public void ReceiveUpdate(StatusUpdate update)
    {
        switch (update.Type)
        {
            case StatusType.NetStatusChanged:
                Dispatcher.UIThread.Post(UpdateBridgesList);
                break;
            case StatusType.NetDevicesChanged:
                Dispatcher.UIThread.Post(UpdateDevicesList);
                break;
        }
    }

    private void UpdateBridgesList()
    {
        BridgesPanel.Children.Clear();
        foreach (var (identifier, bridge) in NetManager.AvailableNetworkBridges)
        {
            BridgesPanel.Children.Add(CreateBridgeRow(identifier, bridge));
        }
    }

    private Avalonia.Controls.Control CreateBridgeRow(string identifier, NetworkBridge bridge)
    {
        var checkBox = new CheckBox { Content = identifier, IsChecked = bridge.IsConnected, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(checkBox);

        ComboBox? options = null;
        if (bridge.ConnectionOptions.Count > 0)
        {
            options = new ComboBox
            {
                ItemsSource = bridge.ConnectionOptions,
                SelectedItem = bridge.CurrentConnectionOption,
                IsEnabled = !bridge.IsConnected,
                MinWidth = 96,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(options, "Baud rate");
            row.Children.Add(options);
        }

        checkBox.Click += (_, _) =>
        {
            try
            {
                if (checkBox.IsChecked == true)
                {
                    NetManager.ConnectBridge(identifier, options?.SelectedItem);
                }
                else
                {
                    NetManager.CloseBridge(identifier);
                }

                ShowError(null);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            {
                Log.LogError(ex, "Unable to change connection for {Bridge}", identifier);
                ShowError(ex.Message);
                checkBox.IsChecked = bridge.IsConnected;
            }
        };
        return row;
    }

    private void UpdateDevicesList()
    {
        DevicesPanel.Children.Clear();
        foreach (var device in NetManager.RecentlySeenDevices)
        {
            DevicesPanel.Children.Add(CreateDevicePanel(device));
        }
    }

    private static Avalonia.Controls.Control CreateDevicePanel(DeviceDescriptor device)
    {
        static TextBox Field(byte value) => new()
        {
            Classes = { "deviceField" },
            Text = value.ToString(),
            IsReadOnly = true,
            Width = 40,
            MinWidth = 0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

        static StackPanel Group(params Avalonia.Controls.Control[] children)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Avalonia.Thickness(0, 0, 12, 4) };
            group.Children.AddRange(children);
            return group;
        }

        var addresses = new WrapPanel();
        addresses.Children.Add(Group(Label("Device Address:"), Field(device.DeviceAddress)));
        addresses.Children.Add(Group(Label("Sub-Addresses:"), Field(device.MinSubAddress), Label("to"), Field(device.MaxSubAddress)));

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock { Text = device.NetworkAddress, FontWeight = FontWeight.SemiBold });
        content.Children.Add(addresses);

        return new Border
        {
            Classes = { "panel" },
            CornerRadius = new Avalonia.CornerRadius(3),
            Padding = new Avalonia.Thickness(8, 6),
            Child = content,
        };
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = message is not null;
    }
}
