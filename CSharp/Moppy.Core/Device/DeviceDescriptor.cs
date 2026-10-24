namespace Moppy.Core.Device;

/// <summary>
/// Meta information for a device on the network (as reported in its pong).
/// </summary>
/// <param name="NetworkAddress">Where the device was seen, e.g. "COM3 - Serial Device" or "239.2.2.7:30994 - 192.168.1.20".</param>
/// <param name="DeviceAddress">The device's address.</param>
/// <param name="MinSubAddress">Lowest sub-address (e.g. drive number) the device responds to.</param>
/// <param name="MaxSubAddress">Highest sub-address the device responds to.</param>
public sealed record DeviceDescriptor(string NetworkAddress, byte DeviceAddress, byte MinSubAddress, byte MaxSubAddress);
