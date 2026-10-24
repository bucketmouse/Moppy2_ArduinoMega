using System.Globalization;
using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace Moppy.Core.Comms.Bridge;

/// <summary>
/// A Serial connection for Moppy devices.  The connection option is the baud rate.
/// </summary>
public sealed class BridgeSerial : NetworkBridge
{
    public const int DefaultBaudRate = 57600;

    /// <summary>
    /// How long the listener waits for data before giving up on a read.  This is just longer than the frequency
    /// of pings by default, so we should expect at least a pong to read this often.
    /// </summary>
    private const int ReadTimeoutMillis = 3200;

    public static readonly IReadOnlyList<int> SupportedBaudRates = [9600, 14400, 19200, 28800, 38400, 57600, 115200];

    private static readonly IReadOnlyList<object> BoxedBaudRates = SupportedBaudRates.Cast<object>().ToArray();

    private readonly SerialPort serialPort;
    private readonly object writeLock = new();
    private Thread? listenerThread;
    private CancellationTokenSource? listenerCancellation;

    public BridgeSerial(string serialPortName)
    {
        serialPort = new SerialPort(serialPortName, DefaultBaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            // Assert DTR / RTS on open like jSerialComm (used by the Java version) does by default.
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = ReadTimeoutMillis,
            WriteTimeout = 1000,
        };
    }

    private static ILogger Log => MoppyLogging.CreateLogger<BridgeSerial>();

    /// <summary>Names of the serial ports currently available on this machine.</summary>
    public static IReadOnlyList<string> GetAvailableSerials()
    {
        try
        {
            return SerialPort.GetPortNames().Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Unable to enumerate serial ports");
            return [];
        }
    }

    public override void Connect() => Connect(DefaultBaudRate);

    public override void Connect(object? connectionOption) =>
        Connect(connectionOption is null ? DefaultBaudRate : Convert.ToInt32(connectionOption, CultureInfo.InvariantCulture));

    /// <exception cref="IOException">If the port could not be opened.</exception>
    public void Connect(int baudRate)
    {
        if (serialPort.IsOpen)
        {
            return;
        }

        try
        {
            serialPort.BaudRate = baudRate;
            try
            {
                serialPort.Open();
            }
            catch (IOException ex) when (serialPort.DtrEnable || serialPort.RtsEnable)
            {
                // Some ports (virtual / Bluetooth serial ports, ptys) don't support modem control lines
                Log.LogInformation(ex, "Opening {Port} without DTR/RTS", serialPort.PortName);
                serialPort.DtrEnable = false;
                serialPort.RtsEnable = false;
                serialPort.Open();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or InvalidOperationException or IOException)
        {
            throw new IOException($"Failed to open serial port {serialPort.PortName}: {ex.Message}", ex);
        }

        // Create and start listener thread
        listenerCancellation = new CancellationTokenSource();
        var token = listenerCancellation.Token;
        listenerThread = new Thread(() => Listen(token))
        {
            IsBackground = true,
            Name = $"Moppy serial listener ({serialPort.PortName})",
        };
        listenerThread.Start();
    }

    public override void SendMessage(MoppyMessage messageToSend)
    {
        lock (writeLock)
        {
            if (!serialPort.IsOpen)
            {
                return;
            }

            try
            {
                serialPort.Write(messageToSend.ToArray(), 0, messageToSend.Length);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                throw new IOException($"Failed to write to serial port {serialPort.PortName}: {ex.Message}", ex);
            }
        }
    }

    public override void Close()
    {
        try
        {
            SendMessage(MoppyMessage.SysStop); // Send a stop message before closing to prevent sticking
        }
        catch (IOException ex)
        {
            Log.LogWarning(ex, "Unable to send stop message before closing {Port}", serialPort.PortName);
        }
        finally
        {
            listenerCancellation?.Cancel();
            lock (writeLock)
            {
                serialPort.Close();
            }

            // Stop and clean up the listener thread
            var thread = listenerThread;
            if (thread is not null && thread != Thread.CurrentThread)
            {
                thread.Join(TimeSpan.FromSeconds(2));
            }

            listenerThread = null;
            listenerCancellation?.Dispose();
            listenerCancellation = null;
        }
    }

    public override string NetworkIdentifier => serialPort.PortName;

    public override bool IsConnected => serialPort.IsOpen;

    public override IReadOnlyList<object> ConnectionOptions => BoxedBaudRates;

    public override object? CurrentConnectionOption => serialPort.BaudRate;

    /// <summary>
    /// Listens to the serial port for MoppyMessages.  Because *all* this thread does is listen for messages,
    /// it's fine to block on reads.
    /// </summary>
    private void Listen(CancellationToken token)
    {
        var buffer = new byte[MoppyMessage.MaxLength];
        buffer[0] = MoppyMessage.StartByte; // We'll be eating the start byte below, so make sure it's here

        while (!token.IsCancellationRequested && serialPort.IsOpen)
        {
            try
            {
                // Keep reading until we get a START_BYTE
                if (serialPort.ReadByte() != MoppyMessage.StartByte)
                {
                    continue;
                }

                // Read address, sub-address, and body size
                ReadExactly(buffer, 1, 3);
                int bodyLength = buffer[3];
                ReadExactly(buffer, 4, bodyLength);

                try
                {
                    AcceptNetworkMessage(MoppyMessageFactory.NetworkReceivedFromBytes(
                        buffer.AsSpan(0, 4 + bodyLength),
                        typeof(BridgeSerial).FullName!,
                        serialPort.PortName,
                        "Serial Device")); // Serial ports don't really have a remote address
                }
                catch (ArgumentException ex)
                {
                    Log.LogWarning(ex, "Exception reading network message");
                }
            }
            catch (TimeoutException)
            {
                // This is fine, we're not always expecting data right away
            }
            catch (Exception) when (token.IsCancellationRequested || !serialPort.IsOpen)
            {
                break; // Port was closed out from under us
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Error reading from serial port {Port}", serialPort.PortName);
                token.WaitHandle.WaitOne(100); // Don't spin if the port is persistently failing
            }
        }
    }

    private void ReadExactly(byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int read = serialPort.Read(buffer, offset, count);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
            count -= read;
        }
    }
}
