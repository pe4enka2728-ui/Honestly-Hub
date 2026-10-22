using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VpnZapretSwitch;

internal static class TunDnsProbe
{
    // Ask the TUN DNS endpoint itself. OS DNS caches can otherwise mask a broken
    // resolver during the post-TUN health check. No physical DNS fallback is used.
    public static async Task<IPAddress> ResolveAsync(string host)
    {
        byte[] id = RandomNumberGenerator.GetBytes(2);
        var query = new List<byte> { id[0], id[1], 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (string label in host.Split('.'))
        { byte[] bytes = Encoding.ASCII.GetBytes(label); query.Add((byte)bytes.Length); query.AddRange(bytes); }
        query.AddRange([0, 0, 1, 0, 1]);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse("172.29.245.1"), 0));
        udp.Connect(IPAddress.Parse("172.29.245.2"), 53);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await udp.SendAsync(query.ToArray(), cancel.Token);
        var response = await udp.ReceiveAsync(cancel.Token);
        return ReadAddress(response.Buffer, id);
    }
    internal static IPAddress ReadAddress(byte[] bytes, byte[] id)
    {
        if (bytes.Length < 12 || bytes[0] != id[0] || bytes[1] != id[1] || (bytes[2] & 0x80) == 0
            || (bytes[2] & 2) != 0 || (bytes[3] & 15) != 0)
            throw new IOException("DNS через TUN вернул ошибку или неполный ответ.");
        int questions = U16(4), answers = U16(6), offset = 12;
        for (int i = 0; i < questions; i++) { SkipName(); Need(4); offset += 4; }
        for (int i = 0; i < answers; i++)
        {
            SkipName(); Need(10);
            int type = U16(offset), cls = U16(offset + 2), length = U16(offset + 8);
            offset += 10; Need(length);
            if (type == 1 && cls == 1 && length == 4) return new IPAddress(bytes.AsSpan(offset, 4));
            offset += length;
        }
        throw new IOException("DNS через TUN не вернул IPv4-адрес.");
        int U16(int at) { if (at < 0 || at + 2 > bytes.Length) throw new IOException("Некорректный DNS-ответ."); return BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at, 2)); }
        void Need(int count) { if (count < 0 || offset + count > bytes.Length) throw new IOException("Неполный DNS-ответ."); }
        void SkipName()
        {
            for (int n = 0; n < 128; n++)
            {
                Need(1); byte length = bytes[offset++];
                if (length == 0) return;
                if ((length & 0xC0) == 0xC0) { Need(1); offset++; return; }
                if (length > 63) break;
                Need(length); offset += length;
            }
            throw new IOException("Некорректное имя в DNS-ответе.");
        }
    }
}
