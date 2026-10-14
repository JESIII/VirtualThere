using Vht.Core.Model;
using Vht.Core.Protocol;

namespace Vht.Core.Tests;

/// <summary>
/// Wire-format tests. Every field offset here is the one documented in the kernel's
/// <c>usbip_protocol.rst</c>, because a stock Linux <c>usbip</c> client parses these bytes
/// positionally and has no way to negotiate a different layout.
/// </summary>
public sealed class CodecTests
{
    [Fact]
    public void OpHeaderRoundTrips()
    {
        var buffer = new byte[UsbIpCodec.OpHeaderSize];
        UsbIpCodec.WriteOpHeader(buffer, UsbIp.OpReqDevlist);

        UsbIpCodec.OpHeader header = UsbIpCodec.ReadOpHeader(buffer);

        Assert.Equal(UsbIp.ProtocolVersion, header.Version);
        Assert.Equal(UsbIp.OpReqDevlist, header.Code);
        Assert.Equal(0u, header.Status);
    }

    [Fact]
    public void OpHeaderUsesBigEndianOnTheWire()
    {
        var buffer = new byte[UsbIpCodec.OpHeaderSize];
        UsbIpCodec.WriteOpHeader(buffer, UsbIp.OpReqImport, 0x01020304);

        // version 0x0111 -> 01 11, code 0x8003 -> 80 03, status 01020304 big-endian.
        Assert.Equal(new byte[] { 0x01, 0x11, 0x80, 0x03, 0x01, 0x02, 0x03, 0x04 }, buffer);
    }

    [Fact]
    public void OpHeaderRejectsAForeignVersion()
    {
        var buffer = new byte[UsbIpCodec.OpHeaderSize];
        BigEndian.WriteU16(buffer, 0, 0x0110);
        BigEndian.WriteU16(buffer, 2, UsbIp.OpReqDevlist);

        UsbIpCodec.OpHeader header = UsbIpCodec.ReadOpHeader(buffer);
        Assert.Throws<UsbIpProtocolException>(() => UsbIpCodec.ValidateVersion(header));
    }

    [Fact]
    public void DevlistReplyRoundTripsEveryField()
    {
        var device = new UsbIpDevice
        {
            Path = "/sys/devices/pci0000:00/usb1/1-2",
            BusId = "1-2",
            BusNum = 1,
            DevNum = 7,
            Speed = UsbIp.SpeedSuper,
            VendorId = 0x1d6b,
            ProductId = 0x0003,
            BcdDevice = 0x0510,
            DeviceClass = UsbClass.PerInterface,
            DeviceSubClass = 0,
            DeviceProtocol = 1,
            ConfigurationValue = 1,
            NumConfigurations = 1,
            NumInterfaces = 2,
            Interfaces = new[]
            {
                new UsbIpInterface { InterfaceClass = 0x08, InterfaceSubClass = 0x06, InterfaceProtocol = 0x50 },
                new UsbIpInterface { InterfaceClass = 0xff, InterfaceSubClass = 0x00, InterfaceProtocol = 0x00 },
            },
            Manufacturer = "VirtualThere",
            Product = "Loopback",
            Serial = "ABC123",
        };

        byte[] frame = UsbIpCodec.EncodeDevlistReply(new[] { device });

        // op_common (8) + ndev (4) + one 312-byte record + two 4-byte interface records.
        Assert.Equal(UsbIpCodec.OpHeaderSize + 4 + UsbIpDevice.WireSize + 8, frame.Length);

        IReadOnlyList<UsbIpDevice> decoded = UsbIpCodec.DecodeDevlistReply(frame);
        UsbIpDevice back = Assert.Single(decoded);

        Assert.Equal(device.Path, back.Path);
        Assert.Equal(device.BusId, back.BusId);
        Assert.Equal(device.BusNum, back.BusNum);
        Assert.Equal(device.DevNum, back.DevNum);
        Assert.Equal(device.Speed, back.Speed);
        Assert.Equal(device.VendorId, back.VendorId);
        Assert.Equal(device.ProductId, back.ProductId);
        Assert.Equal(device.BcdDevice, back.BcdDevice);
        Assert.Equal(device.DeviceClass, back.DeviceClass);
        Assert.Equal(device.DeviceSubClass, back.DeviceSubClass);
        Assert.Equal(device.DeviceProtocol, back.DeviceProtocol);
        Assert.Equal(device.ConfigurationValue, back.ConfigurationValue);
        Assert.Equal(device.NumConfigurations, back.NumConfigurations);
        Assert.Equal(device.NumInterfaces, back.NumInterfaces);
        Assert.Equal(2, back.Interfaces.Count);
        Assert.Equal((byte)0x08, back.Interfaces[0].InterfaceClass);
        Assert.Equal((byte)0xff, back.Interfaces[1].InterfaceClass);
    }

    [Fact]
    public void DeviceRecordFieldOffsetsMatchTheKernelLayout()
    {
        var device = new UsbIpDevice
        {
            Path = "/p",
            BusId = "1-2",
            BusNum = 3,
            DevNum = 9,
            Speed = UsbIp.SpeedHigh,
            VendorId = 0xaaaa,
            ProductId = 0xbbbb,
            BcdDevice = 0xcccc,
            DeviceClass = 0xdd,
            DeviceSubClass = 0xee,
            DeviceProtocol = 0xff,
            ConfigurationValue = 2,
            NumConfigurations = 3,
            NumInterfaces = 0,
        };

        byte[] frame = UsbIpCodec.EncodeDevlistReply(new[] { device });
        int record = UsbIpCodec.OpHeaderSize + 4;

        Assert.Equal(0xaaaa, BigEndian.ReadU16(frame, record + 300));
        Assert.Equal(0xbbbb, BigEndian.ReadU16(frame, record + 302));
        Assert.Equal(0xcccc, BigEndian.ReadU16(frame, record + 304));
        Assert.Equal((byte)0xdd, BigEndian.ReadU8(frame, record + 306));
        Assert.Equal((byte)0xee, BigEndian.ReadU8(frame, record + 307));
        Assert.Equal((byte)0xff, BigEndian.ReadU8(frame, record + 308));
        Assert.Equal((byte)2, BigEndian.ReadU8(frame, record + 309));
        Assert.Equal((byte)3, BigEndian.ReadU8(frame, record + 310));
        Assert.Equal((byte)0, BigEndian.ReadU8(frame, record + 311));
    }

    [Fact]
    public void DevlistReplyWithNoDevicesIsTwelveBytes()
    {
        byte[] frame = UsbIpCodec.EncodeDevlistReply(Array.Empty<UsbIpDevice>());

        Assert.Equal(UsbIpCodec.OpHeaderSize + 4, frame.Length);
        Assert.Empty(UsbIpCodec.DecodeDevlistReply(frame));
    }

    [Fact]
    public void DevlistReplyIsRejectedWhenTheOpCodeIsWrong()
    {
        var buffer = new byte[UsbIpCodec.OpHeaderSize + 4];
        UsbIpCodec.WriteOpHeader(buffer, UsbIp.OpRepImport);
        BigEndian.WriteU32(buffer, UsbIpCodec.OpHeaderSize, 0);

        Assert.Throws<UsbIpProtocolException>(() => UsbIpCodec.DecodeDevlistReply(buffer));
    }

    [Fact]
    public void DevlistReplyTruncatedInsideARecordIsAProtocolError()
    {
        byte[] frame = UsbIpCodec.EncodeDevlistReply(new[] { new UsbIpDevice { BusId = "1-1" } });
        byte[] truncated = frame.AsSpan(0, frame.Length - 4).ToArray();

        Assert.Throws<UsbIpProtocolException>(() => UsbIpCodec.DecodeDevlistReply(truncated));
    }

    [Fact]
    public void ImportReplyCarriesTheDeviceRecord()
    {
        var device = new UsbIpDevice { BusId = "1-4", VendorId = 0x1234, ProductId = 0x5678, NumInterfaces = 0 };

        byte[] frame = UsbIpCodec.EncodeImportReply(device);

        Assert.Equal(UsbIpCodec.OpHeaderSize + UsbIpDevice.WireSize, frame.Length);
        UsbIpCodec.OpHeader header = UsbIpCodec.ReadOpHeader(frame);
        Assert.Equal(UsbIp.OpRepImport, header.Code);
        Assert.Equal(0u, header.Status);

        int offset = UsbIpCodec.OpHeaderSize;
        UsbIpDevice back = UsbIpCodec.ReadDeviceRecord(frame, ref offset);
        Assert.Equal("1-4", back.BusId);
        Assert.Equal(0x1234, back.VendorId);
    }

    [Fact]
    public void ImportErrorIsAnOpHeaderOnly()
    {
        byte[] frame = UsbIpCodec.EncodeImportError(1);

        Assert.Equal(UsbIpCodec.OpHeaderSize, frame.Length);
        UsbIpCodec.OpHeader header = UsbIpCodec.ReadOpHeader(frame);
        Assert.Equal(UsbIp.OpRepImport, header.Code);
        Assert.Equal(1u, header.Status);
    }

    [Fact]
    public void ImportBusIdIsAPaddedThirtyTwoByteField()
    {
        var body = new byte[UsbIp.BusIdLength];
        UsbIpCodec.WriteImportBusId(body, "1-2.3");

        Assert.Equal("1-2.3", UsbIpCodec.ReadImportBusId(body));
        Assert.Equal((byte)'1', body[0]);
        Assert.Equal((byte)0, body[5]);
    }

    [Fact]
    public void TruncatedImportBusIdIsAProtocolError()
    {
        var shortBody = new byte[UsbIp.BusIdLength - 1];
        Assert.Throws<UsbIpProtocolException>(() => UsbIpCodec.ReadImportBusId(shortBody));
    }

    [Fact]
    public void SubmitHeaderRoundTripsAtTheDocumentedOffsets()
    {
        byte[] frame = UsbIpCodec.EncodeCmdSubmit(
            seqNum: 0x0d05,
            devId: 0x0001000f,
            direction: UsbIp.DirIn,
            ep: 1,
            transferFlags: UsbIp.UrbShortNotOk,
            payload: ReadOnlySpan<byte>.Empty,
            setup: 0,
            numberOfPackets: UsbIp.NoIsoPackets,
            transferBufferLength: 64);

        Assert.Equal(UsbIp.HeaderSize, frame.Length);
        Assert.Equal(UsbIp.CmdSubmit, BigEndian.ReadU32(frame, 0));
        Assert.Equal(0x0d05u, BigEndian.ReadU32(frame, 4));
        Assert.Equal(0x0001000fu, BigEndian.ReadU32(frame, 8));
        Assert.Equal(UsbIp.DirIn, BigEndian.ReadU32(frame, 12));
        Assert.Equal(1u, BigEndian.ReadU32(frame, 16));
        Assert.Equal(UsbIp.UrbShortNotOk | UsbIp.UrbDirIn, BigEndian.ReadU32(frame, 20));
        Assert.Equal(64u, BigEndian.ReadU32(frame, 24));
        Assert.Equal(0u, BigEndian.ReadU32(frame, 28));
        Assert.Equal(UsbIp.NoIsoPackets, BigEndian.ReadU32(frame, 32));
        Assert.Equal(0u, BigEndian.ReadU32(frame, 36));
        Assert.Equal(0ul, BigEndian.ReadU64(frame, 40));

        (UsbIpBasicHeader basic, UsbIpCmdSubmit submit, _) = UsbIpCodec.ParseUrbHeader(frame);

        Assert.True(basic.IsIn);
        Assert.Equal((byte)1, basic.EndpointNumber);
        Assert.Equal(1u, basic.BusNum);
        Assert.Equal(15u, basic.DevNum);
        Assert.Equal(64u, submit.TransferBufferLength);
        Assert.False(submit.IsIso);
    }

    [Fact]
    public void UnlinkHeaderRoundTrips()
    {
        byte[] frame = UsbIpCodec.EncodeCmdUnlink(seqNum: 0x0d06, devId: 0x0001000f, unlinkSeqNum: 0x0d05);

        Assert.Equal(UsbIp.HeaderSize, frame.Length);
        Assert.Equal(UsbIp.CmdUnlink, BigEndian.ReadU32(frame, 0));
        Assert.Equal(0x0d05u, BigEndian.ReadU32(frame, 20));

        (UsbIpBasicHeader basic, _, UsbIpCmdUnlink unlink) = UsbIpCodec.ParseUrbHeader(frame);

        Assert.Equal(UsbIp.CmdUnlink, basic.Command);
        Assert.Equal(0x0d06u, basic.SeqNum);
        Assert.Equal(0x0d05u, unlink.UnlinkSeqNum);
    }

    [Fact]
    public void AReturnPacketFromAClientIsRejected()
    {
        var header = new byte[UsbIp.HeaderSize];
        BigEndian.WriteU32(header, 0, UsbIp.RetSubmit);

        Assert.Throws<UsbIpProtocolException>(() => UsbIpCodec.ParseUrbHeader(header));
    }

    [Fact]
    public void AnUnknownUrbCommandIsRejected()
    {
        var header = new byte[UsbIp.HeaderSize];
        BigEndian.WriteU32(header, 0, 0x99);

        Assert.Throws<UsbIpProtocolException>(() => UsbIpCodec.ParseUrbHeader(header));
    }

    [Fact]
    public void AShortUrbHeaderIsAProtocolErrorNotAReadPastTheEnd()
    {
        var header = new byte[UsbIp.HeaderSize - 1];
        Assert.Throws<UsbIpProtocolException>(() => UsbIpCodec.ParseUrbHeader(header));
    }

    [Fact]
    public void AnOversizedTransferBufferIsRejectedBeforeAnyAllocation()
    {
        var submit = new UsbIpCmdSubmit
        {
            TransferBufferLength = (uint)UsbIp.MaxTransferBuffer + 1,
            NumberOfPackets = UsbIp.NoIsoPackets,
        };

        Assert.Throws<UsbIpProtocolException>(() => UsbIpCodec.ValidateSubmit(submit));
    }

    [Fact]
    public void TooManyIsoPacketsIsRejected()
    {
        var submit = new UsbIpCmdSubmit
        {
            TransferBufferLength = 0,
            NumberOfPackets = (uint)UsbIp.MaxIsoPackets + 1,
        };

        Assert.Throws<UsbIpProtocolException>(() => UsbIpCodec.ValidateSubmit(submit));
    }

    [Fact]
    public void APacketCountThatIsNeitherZeroNorAllOnesIsAnIsoCount()
    {
        var submit = new UsbIpCmdSubmit { TransferBufferLength = 0, NumberOfPackets = 7 };

        // usbip_header_cmd_submit.number_of_packets is 0xffffffff for a non-ISO transfer, so a
        // count of 7 *is* an ISO transfer — there is no third state to reject.
        Assert.True(submit.IsIso);
        UsbIpCodec.ValidateSubmit(submit);
    }

    [Fact]
    public void AZeroPacketCountMeansNotIsoAndIsAccepted()
    {
        var submit = new UsbIpCmdSubmit { TransferBufferLength = 0, NumberOfPackets = 0 };
        UsbIpCodec.ValidateSubmit(submit);
        Assert.False(submit.IsIso);
    }

    // -------------------------------------------------------------------------------------
    // Body length: the transfer buffer is present only for an OUT transfer.
    // usbip_protocol.rst, USBIP_CMD_SUBMIT, offset 0x30: "If direction is USBIP_DIR_OUT then
    // n equals transfer_buffer_length; otherwise n equals 0."
    // -------------------------------------------------------------------------------------

    [Fact]
    public void AnInSubmitCarriesNoBodyEvenWhenTheReadSizeIsSet()
    {
        var basic = new UsbIpBasicHeader { Command = UsbIp.CmdSubmit, Direction = UsbIp.DirIn };
        var submit = new UsbIpCmdSubmit { TransferBufferLength = 4096, NumberOfPackets = UsbIp.NoIsoPackets };

        Assert.Equal(0, UsbIpCodec.SubmitBodyLength(basic, submit));
    }

    [Fact]
    public void AnOutSubmitCarriesItsWholePayload()
    {
        var basic = new UsbIpBasicHeader { Command = UsbIp.CmdSubmit, Direction = UsbIp.DirOut };
        var submit = new UsbIpCmdSubmit { TransferBufferLength = 4096, NumberOfPackets = UsbIp.NoIsoPackets };

        Assert.Equal(4096, UsbIpCodec.SubmitBodyLength(basic, submit));
    }

    [Fact]
    public void IsoDescriptorsTrailTheBodyInBothDirections()
    {
        var basic = new UsbIpBasicHeader { Command = UsbIp.CmdSubmit, Direction = UsbIp.DirIn };
        var submit = new UsbIpCmdSubmit { TransferBufferLength = 0, NumberOfPackets = 4 };

        Assert.Equal(4 * UsbIp.IsoPacketDescriptorSize, UsbIpCodec.SubmitBodyLength(basic, submit));
    }

    [Fact]
    public void AnUnlinkHasNoBody()
    {
        var basic = new UsbIpBasicHeader { Command = UsbIp.CmdUnlink };
        Assert.Equal(0, UsbIpCodec.SubmitBodyLength(basic, default));
    }

    [Fact]
    public void RetSubmitCarriesDataAfterTheHeaderOnlyForASuccessfulInTransfer()
    {
        var request = new UsbIpBasicHeader { Command = UsbIp.CmdSubmit, SeqNum = 5, DevId = 1, Direction = UsbIp.DirIn, Ep = 0x81 };
        byte[] data = { 1, 2, 3, 4 };

        byte[] frame = UsbIpCodec.EncodeRetSubmit(request, 0, data);

        Assert.Equal(UsbIp.HeaderSize + 4, frame.Length);
        Assert.Equal(UsbIp.RetSubmit, BigEndian.ReadU32(frame, 0));
        Assert.Equal(5u, BigEndian.ReadU32(frame, 4));
        Assert.Equal(0, BigEndian.ReadI32(frame, 20));
        Assert.Equal(4u, BigEndian.ReadU32(frame, 24));
        Assert.Equal(0u, BigEndian.ReadU32(frame, 36));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, frame[UsbIp.HeaderSize..]);
    }

    [Fact]
    public void RetSubmitOnAnErrorCarriesNoData()
    {
        var request = new UsbIpBasicHeader { Command = UsbIp.CmdSubmit, SeqNum = 5 };
        byte[] frame = UsbIpCodec.EncodeRetSubmit(request, UsbIp.StatusEconnreset, ReadOnlySpan<byte>.Empty);

        Assert.Equal(UsbIp.HeaderSize, frame.Length);
        Assert.Equal(UsbIp.StatusEconnreset, BigEndian.ReadI32(frame, 20));
        Assert.Equal(0u, BigEndian.ReadU32(frame, 24));
    }

    [Fact]
    public void RetUnlinkCarriesTheStatusAtTheSubmitStatusOffset()
    {
        var request = new UsbIpBasicHeader { Command = UsbIp.CmdUnlink, SeqNum = 6 };

        byte[] frame = UsbIpCodec.EncodeRetUnlink(request, UsbIp.StatusEconnreset);

        Assert.Equal(UsbIp.HeaderSize, frame.Length);
        Assert.Equal(UsbIp.RetUnlink, BigEndian.ReadU32(frame, 0));
        Assert.Equal(6u, BigEndian.ReadU32(frame, 4));
        Assert.Equal(UsbIp.StatusEconnreset, BigEndian.ReadI32(frame, 20));
    }

    [Fact]
    public void IsoDescriptorsRoundTripThroughARetSubmit()
    {
        var request = new UsbIpBasicHeader { Command = UsbIp.CmdSubmit, SeqNum = 1 };
        var packets = new[]
        {
            new IsoPacketDescriptor { Offset = 0, Length = 64, ActualLength = 64, Status = 0 },
            new IsoPacketDescriptor { Offset = 64, Length = 64, ActualLength = 0, Status = -32 },
        };

        byte[] frame = UsbIpCodec.EncodeRetSubmit(request, 0, ReadOnlySpan<byte>.Empty, numberOfPackets: 2, packets: packets);

        Assert.Equal(UsbIp.HeaderSize + 2 * UsbIp.IsoPacketDescriptorSize, frame.Length);

        IReadOnlyList<IsoPacketDescriptor> back =
            UsbIpCodec.ParseIsoDescriptors(frame.AsSpan(UsbIp.HeaderSize), 2);

        Assert.Equal(2, back.Count);
        Assert.Equal(64u, back[1].Offset);
        Assert.Equal(-32, back[1].Status);
    }

    [Fact]
    public void TheFrameAllocationCeilingIsExplicit()
    {
        Assert.Equal(
            UsbIp.MaxTransferBuffer + UsbIp.HeaderSize + UsbIp.MaxIsoPackets * UsbIp.IsoPacketDescriptorSize,
            UsbIpCodec.MaxFrameAllocation);
    }

    [Fact]
    public void TheProtocolVersionIsTheKernelDocumentedOne()
    {
        Assert.Equal(0x0111, UsbIp.ProtocolVersion);
        Assert.Equal(VhtVersion.UsbIpProtocolVersion, UsbIp.ProtocolVersion);
    }

    // ---------------------------------------------------------------------------------------
    // VirtualThere's own device-info extension (docs/SERVER.md §8)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheDeviceInfoExtensionRoundTripsNamesAndOmissions()
    {
        byte[] reply = UsbIpCodec.EncodeVhtDeviceInfoReply(new[]
        {
            new UsbIpCodec.VhtDeviceInfo("1-1", "Label printer"),
            new UsbIpCodec.VhtDeviceInfo("1-2", null),
            new UsbIpCodec.VhtDeviceInfo("2-3", "Büro-Stick ★"),
        });

        // op_common + magic + count, then 33 bytes of busid/length per entry plus the names.
        Assert.Equal(UsbIp.OpRepVhtDevInfo, BigEndian.ReadU16(reply, 2));
        Assert.Equal(UsbIp.VhtDevInfoMagic, BigEndian.ReadU32(reply, UsbIpCodec.OpHeaderSize));
        Assert.Equal(3u, BigEndian.ReadU32(reply, UsbIpCodec.OpHeaderSize + 4));

        IReadOnlyList<UsbIpCodec.VhtDeviceInfo> back = UsbIpCodec.DecodeVhtDeviceInfoReply(reply);

        Assert.Equal(3, back.Count);
        Assert.Equal("1-1", back[0].BusId);
        Assert.Equal("Label printer", back[0].Nickname);
        Assert.Null(back[1].Nickname);                 // omitted, not an empty string
        Assert.Equal("Büro-Stick ★", back[2].Nickname); // UTF-8, and no ASCII coercion
    }

    [Fact]
    public void ALongerNicknameIsTruncatedOnACodepointBoundary()
    {
        // 100 two-byte characters is 200 UTF-8 bytes, above the 96-byte ceiling. The truncation
        // must stop at a codepoint boundary, or the far side decodes replacement characters.
        string long_ = string.Concat(Enumerable.Repeat('é', 100));

        byte[] encoded = UsbIpCodec.EncodeNickname(long_);

        Assert.True(encoded.Length <= UsbIp.VhtNicknameMaxBytes);
        Assert.Equal(UsbIp.VhtNicknameMaxBytes, encoded.Length);
        Assert.Equal(new string('é', UsbIp.VhtNicknameMaxBytes / 2),
            System.Text.Encoding.UTF8.GetString(encoded));

        // ...and it survives the frame itself, with no bytes left over.
        byte[] reply = UsbIpCodec.EncodeVhtDeviceInfoReply(new[] { new UsbIpCodec.VhtDeviceInfo("1-1", long_) });
        Assert.Equal(UsbIpCodec.OpHeaderSize + 8 + UsbIpCodec.VhtDeviceInfoEntryHeaderSize + encoded.Length,
            reply.Length);
        Assert.Equal(new string('é', UsbIp.VhtNicknameMaxBytes / 2),
            UsbIpCodec.DecodeVhtDeviceInfoReply(reply)[0].Nickname);
    }

    [Fact]
    public void ADeviceInfoEntryThatOverrunsTheFrameIsAProtocolError()
    {
        byte[] reply = UsbIpCodec.EncodeVhtDeviceInfoReply(new[] { new UsbIpCodec.VhtDeviceInfo("1-1", "abcd") });

        // Claim a name longer than the frame holds: the decoder must refuse rather than read past it.
        BigEndian.WriteU8(reply, UsbIpCodec.OpHeaderSize + 8 + UsbIp.BusIdLength, 96);

        Assert.Throws<UsbIpProtocolException>(() => UsbIpCodec.DecodeVhtDeviceInfoReply(reply));

        // And a name length above the documented ceiling is refused before any read at all.
        byte[] overLong = UsbIpCodec.EncodeVhtDeviceInfoReply(new[] { new UsbIpCodec.VhtDeviceInfo("1-1", "abcd") });
        BigEndian.WriteU8(overLong, UsbIpCodec.OpHeaderSize + 8 + UsbIp.BusIdLength, 200);
        Assert.Throws<UsbIpProtocolException>(() => UsbIpCodec.DecodeVhtDeviceInfoReply(overLong));
    }

    [Fact]
    public void TheDeviceInfoReplyIsRefusedWithoutTheExtensionMagic()
    {
        byte[] reply = UsbIpCodec.EncodeVhtDeviceInfoReply(Array.Empty<UsbIpCodec.VhtDeviceInfo>());
        BigEndian.WriteU32(reply, UsbIpCodec.OpHeaderSize, 0);   // not 'VHT1'

        Assert.Throws<UsbIpProtocolException>(() => UsbIpCodec.DecodeVhtDeviceInfoReply(reply));
    }
}
