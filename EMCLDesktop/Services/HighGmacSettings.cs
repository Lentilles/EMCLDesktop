using System.Collections.Immutable;
using System.Globalization;
using EMCL.Protocol.Security;

namespace EMCLDesktop.Services;

public sealed record HighGmacSettings(string AuthenticationKey, string BlockCipherKey,
    string ClientSystemTitle, string ServerSystemTitle, string InitialCounter)
{
    public DlmsSecurityProtocol CreateProtocol(IInvocationCounterStore counters) => new(new DlmsSecurityOptions
    {
        Suite = DlmsSecuritySuite.AesGcm128,
        AuthenticationKey = ParseHex(AuthenticationKey, 16, "Ключ аутентификации"),
        GlobalKey = ParseHex(BlockCipherKey, 16, "Ключ шифрования"),
        LocalSystemTitle = ParseHex(ClientSystemTitle, 8, "System Title клиента"),
        PeerSystemTitle = ParseHex(ServerSystemTitle, 8, "System Title прибора"),
        Counters = counters
    });

    public uint ParseInitialCounter()
    {
        if (!uint.TryParse(InitialCounter, NumberStyles.None, CultureInfo.InvariantCulture, out uint counter) || counter == uint.MaxValue)
            throw new ArgumentException("Начальный счётчик должен быть целым числом от 0 до 4294967294.");
        return counter;
    }

    private static ImmutableArray<byte> ParseHex(string value, int length, string name)
    {
        try
        {
            var bytes = Convert.FromHexString(value.Trim());
            if (bytes.Length == length) return bytes.ToImmutableArray();
        }
        catch (FormatException) { }
        throw new ArgumentException($"{name}: укажите {length} байт в HEX без разделителей.");
    }
}
