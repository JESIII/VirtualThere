// SPDX-License-Identifier: Apache-2.0
//
// The two USB/IP *op* exchanges the client performs itself: list a peer's exported devices, and
// import one (attach). Runtime URB traffic is never parsed here — the UDE driver owns it and the
// loopback bridge relays it opaquely (ARCHITECTURE.md §3.3, §6.2).
//
// Wire layouts (all big-endian, all packed):
//
//   OP_REQ_DEVLIST   op_common{version,code=0x8005,status} + u32 reserved                = 12 bytes
//   OP_REP_DEVLIST   op_common{code=0x0005,status} + u32 ndev
//                    + ndev * usbip_usb_device(312)
//                    + per device, bNumInterfaces * usbip_usb_interface(4)
//   OP_REQ_IMPORT    op_common{code=0x8003} + char busid[32]                             = 40 bytes
//   OP_REP_IMPORT    op_common{code=0x0003,status} + usbip_usb_device(312)               = 320 bytes
//                    (the device record is present even when status != 0; only trust it on 0)

using Vht.Client.Transport;

namespace Vht.Client.Usbip;

/// <summary>Result of an OP_REQ_IMPORT: the peer's status and, on success, the bound device.</summary>
internal sealed record ImportResult(OpStatus Status, UsbipDeviceRecord? Device)
{
    internal static ImportResult From(OpStatus status, UsbipDeviceRecord? device) => new(status, device);

    internal string DescribeStatus() => Status switch
    {
        OpStatus.Ok => "attached",
        OpStatus.NotAvailable => "the peer reports the device is no longer available",
        OpStatus.DeviceBusy => "the peer reports the device is in use elsewhere",
        OpStatus.DeviceError => "the peer reports the device is in an error state",
        OpStatus.NoDevice => "the peer has no device with that busid",
        _ => "the peer returned an unspecified error",
    };
}

/// <summary>Reads and writes whole USB/IP op messages on a transport.</summary>
internal static class UsbipOp
{
    /// <summary>Sends OP_REQ_DEVLIST and parses OP_REP_DEVLIST.</summary>
    internal static async Task<IReadOnlyList<UsbipDeviceRecord>> ListDevicesAsync(
        IClientTransport transport,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var request = new byte[UsbipProtocol.DevListRequestSize];
        BigEndian.WriteU16(request, UsbipProtocol.Version);
        BigEndian.WriteU16(request.AsSpan(2), UsbipProtocol.OpReqDevList);
        BigEndian.WriteU32(request.AsSpan(4), 0u);   // status: 0 in a request
        BigEndian.WriteU32(request.AsSpan(8), 0u);   // op_devlist_request._reserved
        await transport.WriteAsync(request, ct).ConfigureAwait(false);

        var head = new byte[UsbipProtocol.DevListRequestSize];
        await ReadExactlyAsync(transport, head, ct, "OP_REP_DEVLIST header").ConfigureAwait(false);

        RequireReply(head, UsbipProtocol.OpRepDevList);

        uint deviceCount = BigEndian.ReadU32(head.AsSpan(UsbipProtocol.OpCommonSize));
        if (deviceCount > UsbipProtocol.MaxDevlistRecordsPerReply)
        {
            // Bounded parse: a length field is data, not an allocation instruction (§6.1, §10 risk 10).
            throw new UsbipProtocolException(
                $"peer announced {deviceCount} devices, above the {UsbipProtocol.MaxDevlistRecordsPerReply} " +
                "record parse bound. Refusing to allocate from an unvalidated length.");
        }

        var devices = new List<UsbipDeviceRecord>((int)Math.Min(deviceCount, 64));
        var recordBuffer = new byte[UsbipProtocol.UsbipUsbDeviceSize];
        var interfaceBuffer = new byte[UsbipProtocol.UsbipUsbInterfaceSize];

        for (uint i = 0; i < deviceCount; i++)
        {
            await ReadExactlyAsync(transport, recordBuffer, ct, $"device record #{i}").ConfigureAwait(false);
            UsbipDeviceRecord partial = ParseDeviceRecord(recordBuffer);

            var interfaces = new List<UsbipInterface>(partial.NumInterfaces);
            for (int j = 0; j < partial.NumInterfaces; j++)
            {
                await ReadExactlyAsync(transport, interfaceBuffer, ct, $"interface #{j} of device #{i}")
                    .ConfigureAwait(false);
                interfaces.Add(new UsbipInterface(interfaceBuffer[0], interfaceBuffer[1], interfaceBuffer[2]));
            }

            devices.Add(partial with { Interfaces = interfaces });
        }

        return devices;
    }

    /// <summary>
    /// Sends OP_REQ_IMPORT for one busid and parses OP_REP_IMPORT. `busid` here is the *routing key*
    /// the loopback bridge resolves — for a plain single-peer setup it is the peer's own busid.
    /// </summary>
    internal static async Task<ImportResult> ImportAsync(
        IClientTransport transport,
        string busId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrEmpty(busId);

        var request = new byte[UsbipProtocol.ImportRequestSize];
        BigEndian.WriteU16(request, UsbipProtocol.Version);
        BigEndian.WriteU16(request.AsSpan(2), UsbipProtocol.OpReqImport);
        BigEndian.WriteU32(request.AsSpan(4), 0u);
        try
        {
            BigEndian.WriteCString(request.AsSpan(UsbipProtocol.ImportBusIdOffset, UsbipProtocol.BusIdSize), busId);
        }
        catch (ArgumentException ex)
        {
            throw new UsbipProtocolException($"busid '{busId}' does not fit the 32-byte protocol field: {ex.Message}");
        }
        await transport.WriteAsync(request, ct).ConfigureAwait(false);

        var reply = new byte[UsbipProtocol.ImportReplySize];
        await ReadExactlyAsync(transport, reply, ct, "OP_REP_IMPORT").ConfigureAwait(false);

        RequireReply(reply, UsbipProtocol.OpRepImport);

        var status = (OpStatus)BigEndian.ReadU32(reply.AsSpan(4));
        if (status != OpStatus.Ok)
        {
            return ImportResult.From(status, null);
        }

        UsbipDeviceRecord device =
            ParseDeviceRecord(reply.AsSpan(UsbipProtocol.OpCommonSize, UsbipProtocol.UsbipUsbDeviceSize));
        return ImportResult.From(status, device);
    }

    /// <summary>Parses one 312-byte usbip_usb_device. Field offsets are fixed by the spec.</summary>
    internal static UsbipDeviceRecord ParseDeviceRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length < UsbipProtocol.UsbipUsbDeviceSize)
        {
            throw new UsbipProtocolException(
                $"device record is {record.Length} bytes, expected {UsbipProtocol.UsbipUsbDeviceSize}.");
        }

        int offset = 0;
        string path = BigEndian.ReadCString(record[offset..(offset + UsbipProtocol.DevPathMax)]);
        offset += UsbipProtocol.DevPathMax;
        string busId = BigEndian.ReadCString(record[offset..(offset + UsbipProtocol.BusIdSize)]);
        offset += UsbipProtocol.BusIdSize;

        uint busNum = BigEndian.ReadU32(record[offset..]); offset += 4;
        uint devNum = BigEndian.ReadU32(record[offset..]); offset += 4;
        uint speed = BigEndian.ReadU32(record[offset..]); offset += 4;
        ushort vendor = BigEndian.ReadU16(record[offset..]); offset += 2;
        ushort product = BigEndian.ReadU16(record[offset..]); offset += 2;
        ushort bcdDevice = BigEndian.ReadU16(record[offset..]); offset += 2;

        byte deviceClass = record[offset]; offset += 1;
        byte deviceSubClass = record[offset]; offset += 1;
        byte deviceProtocol = record[offset]; offset += 1;
        byte configurationValue = record[offset]; offset += 1;
        byte numConfigurations = record[offset]; offset += 1;
        byte numInterfaces = record[offset];

        return new UsbipDeviceRecord
        {
            Path = path,
            BusId = busId,
            BusNum = busNum,
            DevNum = devNum,
            Speed = Enum.IsDefined(typeof(UsbDeviceSpeed), speed) ? (UsbDeviceSpeed)speed : UsbDeviceSpeed.Unknown,
            VendorId = vendor,
            ProductId = product,
            BcdDevice = bcdDevice,
            DeviceClass = deviceClass,
            DeviceSubClass = deviceSubClass,
            DeviceProtocol = deviceProtocol,
            ConfigurationValue = configurationValue,
            NumConfigurations = numConfigurations,
            NumInterfaces = numInterfaces,
        };
    }

    /// <summary>Validates an op_common reply header: version, op-code (including the reply bit), status.</summary>
    private static void RequireReply(ReadOnlySpan<byte> header, ushort expectedCode)
    {
        ushort version = BigEndian.ReadU16(header);
        if (version != UsbipProtocol.Version)
        {
            throw new UsbipProtocolException(
                $"peer speaks USB/IP version 0x{version:x4}, this client speaks 0x{UsbipProtocol.Version:x4}.");
        }

        ushort code = BigEndian.ReadU16(header[2..]);
        if (code != expectedCode)
        {
            throw new UsbipProtocolException(
                $"expected op-code 0x{expectedCode:x4}, peer sent 0x{code:x4}. " +
                "A request code where a reply is required means the peer is confused or hostile.");
        }

        uint status = BigEndian.ReadU32(header[4..]);
        if (status != 0 && expectedCode == UsbipProtocol.OpRepDevList)
        {
            throw new UsbipProtocolException($"peer refused OP_REQ_DEVLIST with status {status}.");
        }
    }

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes or throws. 0 bytes means the peer closed.</summary>
    internal static async Task ReadExactlyAsync(
        IClientTransport transport,
        Memory<byte> buffer,
        CancellationToken ct,
        string what)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await transport.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new UsbipProtocolException(
                    $"peer closed the connection after {read} of {buffer.Length} bytes while reading {what}.");
            }
            read += n;
        }
    }
}
