using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Svyaz.Core;

public sealed record NativeProbeResult(string Service, bool Healthy, string Detail, long ElapsedMs, DateTimeOffset CheckedAt);

public static class TelegramProbe
{
    // req_pq_multi only: no authentication key, account, login or user messages.
    // https://core.telegram.org/mtproto/mtproto-transports#transport-obfuscation
    public static async Task<NativeProbeResult> CheckAsync(AppConfig config, CancellationToken token,
        string target = "149.154.167.51", Func<string, int, CancellationToken, ValueTask<Stream>>? connector = null,
        byte[]? proxySecret = null, short dc = 2)
    {
        var watch = Stopwatch.StartNew();
        var stage = "TCP / SOCKS5";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await using var stream = await (connector?.Invoke(target, 443, deadline.Token) ?? TelegramRoute.ConnectAsync(target, 443, config, deadline.Token));
            stage = "ответ MTProto";
            byte[] init;
            do { init = RandomNumberGenerator.GetBytes(64); }
            while (init[0] == 0xef || BinaryPrimitives.ReadUInt32LittleEndian(init) is 0xdddddddd or 0xeeeeeeee or 0x44414548 or 0x54534f50 or 0x20544547 or 0x4954504f or 0x02010316 ||
                   BinaryPrimitives.ReadUInt32LittleEndian(init.AsSpan(4)) == 0);
            init.AsSpan(56, 4).Fill(0xef);
            if (proxySecret != null) BinaryPrimitives.WriteInt16LittleEndian(init.AsSpan(60), dc);
            var reverse = init.Reverse().ToArray();
            using var encrypt = new AesCounter(proxySecret == null ? init[8..40] : SHA256.HashData(init[8..40].Concat(proxySecret).ToArray()), init[40..56]);
            using var decrypt = new AesCounter(proxySecret == null ? reverse[8..40] : SHA256.HashData(reverse[8..40].Concat(proxySecret).ToArray()), reverse[40..56]);
            var encryptedInit = encrypt.Apply(init);
            encryptedInit.AsSpan(56, 8).CopyTo(init.AsSpan(56));
            await stream.WriteAsync(init, deadline.Token);
            var nonce = RandomNumberGenerator.GetBytes(16);
            var request = new byte[41]; request[0] = 10;
            var now = DateTimeOffset.UtcNow;
            var id = ((ulong)now.ToUnixTimeSeconds() << 32) | ((ulong)(now.Millisecond * (1L << 32) / 1000) & ~3UL) | 4UL;
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(9), id);
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(17), 20);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(21), 0xbe7e8ef1);
            nonce.CopyTo(request, 25);
            await stream.WriteAsync(encrypt.Apply(request), deadline.Token);
            var size = new byte[1]; await stream.ReadExactlyAsync(size, deadline.Token);
            var words = (int)decrypt.Apply(size)[0];
            if (words == 127)
            {
                var extended = new byte[3]; await stream.ReadExactlyAsync(extended, deadline.Token); extended = decrypt.Apply(extended);
                words = extended[0] | extended[1] << 8 | extended[2] << 16;
            }
            if (words is < 15 or > 1024) throw new IOException("Сервер отклонил MTProto-запрос.");
            var body = new byte[words * 4]; await stream.ReadExactlyAsync(body, deadline.Token); body = decrypt.Apply(body);
            if (BinaryPrimitives.ReadUInt64LittleEndian(body) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)) != 0x05162463 ||
                !body.AsSpan(24, 16).SequenceEqual(nonce)) throw new IOException("Неожиданный ответ MTProto.");
            return new("Telegram Desktop", true, "Получен MTProto resPQ. Вход и сообщения не проверялись.", watch.ElapsedMilliseconds, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new("Telegram Desktop", false, "Тайм-аут: " + stage, watch.ElapsedMilliseconds, DateTimeOffset.Now); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        { return new("Telegram Desktop", false, stage + ": " + ex.Message, watch.ElapsedMilliseconds, DateTimeOffset.Now); }
    }
}

public sealed class AesCounter : IDisposable
{
    private readonly Aes aes;
    private readonly byte[] counter;
    private readonly byte[] block = new byte[16];
    private int offset = 16;
    public AesCounter(byte[] key, byte[] iv)
    { aes = Aes.Create(); aes.Key = key; counter = (byte[])iv.Clone(); }
    public byte[] Apply(byte[] bytes)
    {
        var output = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (offset == 16)
            {
                aes.EncryptEcb(counter, block, PaddingMode.None); offset = 0;
                for (var j = 15; j >= 0 && ++counter[j] == 0; j--) { }
            }
            output[i] = (byte)(bytes[i] ^ block[offset++]);
        }
        return output;
    }
    public void Dispose() { aes.Dispose(); CryptographicOperations.ZeroMemory(counter); CryptographicOperations.ZeroMemory(block); }
}
