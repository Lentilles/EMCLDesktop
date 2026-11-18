using System.Buffers;
using System.Collections.Immutable;
using EMCL.Framing.Hdlc.Frames;

namespace EMCLDesktop.Tests;

internal static class PacketTestData
{
    internal static readonly byte[] GetRequestApdu = Convert.FromHexString("C001C100030100200700FF0200");
    internal static readonly byte[] GetResponseApdu = Convert.FromHexString("C401C1001208FD");
    internal static readonly byte[] RequestObisBytes = [1, 0, 32, 7, 0, 255];

    internal static string FormatHex(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2")));

    internal static byte[] CreateHdlcFrame(byte[] information, bool rx = false, bool segmented = false, byte sequence = 0, byte? control = null,
        bool oneByteServer = false)
    {
        var writer = new ArrayBufferWriter<byte>();
        var client = HdlcAddress.Client(48);
        var server = oneByteServer ? HdlcAddress.Server(1) : HdlcAddress.Server(1, 17);
        new HdlcCodec().Encode(new(rx ? client : server, rx ? server : client,
            control ?? (byte)(sequence << 1), information.ToImmutableArray(), segmented), writer);
        return writer.WrittenSpan.ToArray();
    }

    internal static byte[] CreateWrapperFrame(byte[] apdu, bool rx = false) => [0, 1, 0, rx ? (byte)1 : (byte)48, 0, rx ? (byte)48 : (byte)1,
        (byte)(apdu.Length >> 8), (byte)apdu.Length, .. apdu];
}
