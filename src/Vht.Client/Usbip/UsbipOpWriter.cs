// SPDX-License-Identifier: Apache-2.0
//
// Writes the two reply frames the client must be able to synthesise rather than only parse:
//
//  * OP_REP_DEVLIST — the loopback bridge answers the driver's/userspace tooling's device-list
//    request itself, aggregating every configured peer into one reply, so it must be able to build
//    the frame. Records are re-emitted from the parsed model, which is also what lets a device's
//    busid be rewritten to its routing key.
//  * OP_REP_IMPORT is never synthesised: the peer's own reply is relayed byte-for-byte (see
//    LoopbackBridge), because the driver validates it and the peer is authoritative.
//
// Round-tripping through this writer is asserted against the parser in UsbipOpTests.

using Vht.Client.Usbip;

namespace Vht.Client.Usbip;

/// <summary>Serialises USB/IP frames this client originates.</summary>
internal static class UsbipOpWriter
{
    /// <summary>
    /// Builds an OP_REP_DEVLIST reply for <paramref name="devices"/>. Each device's busid is written
    /// as <paramref name="busIdOverride"/> when supplied (the bridge's routing-key rewrite) or as the
    /// record's own busid otherwise.
    /// </summary>
    internal static byte[] BuildDevListReply(IReadOnlyList<(UsbipDeviceRecord Device, string BusId)> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        int size = UsbipProtocol.DevListRequestSize;
        foreach ((UsbipDeviceRecord device, _) in devices)
        {
            size += UsbipProtocol.UsbipUsbDeviceSize
                  + (device.NumInterfaces * UsbipProtocol.UsbipUsbInterfaceSize);
        }

        var buffer = new byte[size];
        BigEndian.WriteU16(buffer, UsbipProtocol.Version);
        BigEndian.WriteU16(buffer.AsSpan(2), UsbipProtocol.OpRepDevList);
        BigEndian.WriteU32(buffer.AsSpan(4), 0u);                                   // status = OK
        BigEndian.WriteU32(buffer.AsSpan(8), (uint)devices.Count);                  // ndev

        int offset = UsbipProtocol.DevListRequestSize;
        foreach ((UsbipDeviceRecord device, string busId) in devices)
        {
            WriteDeviceRecord(buffer.AsSpan(offset, UsbipProtocol.UsbipUsbDeviceSize), device, busId);
            offset += UsbipProtocol.UsbipUsbDeviceSize;

            foreach (UsbipInterface iface in device.Interfaces)
            {
                buffer[offset] = iface.InterfaceClass;
                buffer[offset + 1] = iface.InterfaceSubClass;
                buffer[offset + 2] = iface.InterfaceProtocol;
                buffer[offset + 3] = 0;                                             // padding
                offset += UsbipProtocol.UsbipUsbInterfaceSize;
            }
        }

        return buffer;
    }

    /// <summary>Builds a bare OP_REQ_DEVLIST request (the client's own listing path).</summary>
    internal static byte[] BuildDevListRequest()
    {
        var buffer = new byte[UsbipProtocol.DevListRequestSize];
        BigEndian.WriteU16(buffer, UsbipProtocol.Version);
        BigEndian.WriteU16(buffer.AsSpan(2), UsbipProtocol.OpReqDevList);
        BigEndian.WriteU32(buffer.AsSpan(4), 0u);
        BigEndian.WriteU32(buffer.AsSpan(8), 0u);
        return buffer;
    }

    /// <summary>Builds an OP_REQ_IMPORT request for one busid.</summary>
    internal static byte[] BuildImportRequest(string busId)
    {
        var buffer = new byte[UsbipProtocol.ImportRequestSize];
        BigEndian.WriteU16(buffer, UsbipProtocol.Version);
        BigEndian.WriteU16(buffer.AsSpan(2), UsbipProtocol.OpReqImport);
        BigEndian.WriteU32(buffer.AsSpan(4), 0u);
        BigEndian.WriteCString(buffer.AsSpan(UsbipProtocol.ImportBusIdOffset, UsbipProtocol.BusIdSize), busId);
        return buffer;
    }

    /// <summary>Writes one 312-byte usbip_usb_device at the fixed offsets the parser reads.</summary>
    internal static void WriteDeviceRecord(Span<byte> destination, UsbipDeviceRecord device, string? busIdOverride = null)
    {
        if (destination.Length < UsbipProtocol.UsbipUsbDeviceSize)
        {
            throw new ArgumentException(
                $"destination is {destination.Length} bytes, need {UsbipProtocol.UsbipUsbDeviceSize}.",
                nameof(destination));
        }

        Span<byte> record = destination[..UsbipProtocol.UsbipUsbDeviceSize];
        record.Clear();

        int offset = 0;
        BigEndian.WriteCString(record[offset..(offset + UsbipProtocol.DevPathMax)], device.Path);
        offset += UsbipProtocol.DevPathMax;
        BigEndian.WriteCString(
            record[offset..(offset + UsbipProtocol.BusIdSize)],
            busIdOverride ?? device.BusId);
        offset += UsbipProtocol.BusIdSize;

        BigEndian.WriteU32(record[offset..], device.BusNum); offset += 4;
        BigEndian.WriteU32(record[offset..], device.DevNum); offset += 4;
        BigEndian.WriteU32(record[offset..], (uint)device.Speed); offset += 4;
        BigEndian.WriteU16(record[offset..], device.VendorId); offset += 2;
        BigEndian.WriteU16(record[offset..], device.ProductId); offset += 2;
        BigEndian.WriteU16(record[offset..], device.BcdDevice); offset += 2;

        record[offset] = device.DeviceClass; offset += 1;
        record[offset] = device.DeviceSubClass; offset += 1;
        record[offset] = device.DeviceProtocol; offset += 1;
        record[offset] = device.ConfigurationValue; offset += 1;
        record[offset] = device.NumConfigurations; offset += 1;
        record[offset] = device.NumInterfaces;
    }
}
