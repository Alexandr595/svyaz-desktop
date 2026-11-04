using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Svyaz.Core;

// Local MTProxy transport adapter. The account's MTProto payload stays encrypted.
// Only the outer obfuscation is adapted to Telegram's documented WSS transport.
public sealed class TelegramDesktopBridge(AppConfig config, string directory) : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, config.TelegramLocalPort);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<int, TcpClient> clients = new();
    private readonly SemaphoreSlim slots = new(48);
    private int serial;
    private int disposed;
    private long sent;
    private long received;
    public byte[] Secret { get; } = LoadSecret(directory);
    public int Port { get; private set; }
    public event Action<string>? Error;
    public bool IsRunning => Port != 0 && Volatile.Read(ref disposed) == 0;
    public ProxyTraffic Traffic => new(clients.Count, serial, Interlocked.Read(ref sent), Interlocked.Read(ref received));
    public string Link => $"tg://proxy?server=127.0.0.1&port={Port}&secret={Convert.ToHexString(Secret).ToLowerInvariant()}";

    private static byte[] LoadSecret(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "telegram-local-secret.txt");
        if (File.Exists(path))
        {
            var existing = Convert.FromHexString(File.ReadAllText(path).Trim());
            if (existing.Length != 16) throw new IOException("Повреждён локальный секрет Telegram.");
            return existing;
        }
        var bytes = RandomNumberGenerator.GetBytes(16);
        File.WriteAllText(path, Convert.ToHexString(bytes));
        return bytes;
    }
    public void Start()
    {
        if (Port != 0) throw new InvalidOperationException("Локальный MTProxy уже запущен.");
        listener.Start(48); Port = ((IPEndPoint)listener.LocalEndpoint).Port; _ = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref serial); clients[id] = client; _ = ServeAsync(id, client);
            }
        }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception ex) { Error?.Invoke("MTProxy listener: " + ex.Message); }
    }
    private async Task ServeAsync(int id, TcpClient client)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        try
        {
            client.NoDelay = true;
            await using var input = client.GetStream();
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var init = new byte[64]; await input.ReadExactlyAsync(init, handshake.Token);
            var reverse = init.Reverse().ToArray();
            using var clientDecrypt = new AesCounter(SHA256.HashData(init[8..40].Concat(Secret).ToArray()), init[40..56]);
            using var clientEncrypt = new AesCounter(SHA256.HashData(reverse[8..40].Concat(Secret).ToArray()), reverse[40..56]);
            var plain = clientDecrypt.Apply(init);
            var protocol = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(56));
            if (protocol is not (0xefefefef or 0xeeeeeeee or 0xdddddddd)) throw new IOException("Некорректный MTProxy-заголовок или локальный секрет.");
            var dc = BinaryPrimitives.ReadInt16LittleEndian(plain.AsSpan(60));
            if (Math.Abs((int)dc) is < 1 or > 5) throw new IOException("MTProxy: неизвестный дата-центр.");
            await using var output = await TelegramWebSocket.ConnectAsync(dc, config, handshake.Token);
            byte[] remoteInit;
            do { remoteInit = RandomNumberGenerator.GetBytes(64); }
            while (remoteInit[0] == 0xef || BinaryPrimitives.ReadUInt32LittleEndian(remoteInit) is 0xdddddddd or 0xeeeeeeee or 0x44414548 or 0x54534f50 or 0x20544547 or 0x4954504f or 0x02010316 ||
                   BinaryPrimitives.ReadUInt32LittleEndian(remoteInit.AsSpan(4)) == 0);
            BinaryPrimitives.WriteUInt32LittleEndian(remoteInit.AsSpan(56), protocol);
            var remoteReverse = remoteInit.Reverse().ToArray();
            using var remoteEncrypt = new AesCounter(remoteInit[8..40], remoteInit[40..56]);
            using var remoteDecrypt = new AesCounter(remoteReverse[8..40], remoteReverse[40..56]);
            var encryptedInit = remoteEncrypt.Apply(remoteInit);
            encryptedInit.AsSpan(56, 8).CopyTo(remoteInit.AsSpan(56));
            await output.WriteAsync(remoteInit, handshake.Token);
            var upload = RelayAsync(input, output, clientDecrypt, remoteEncrypt, true, connection.Token);
            var download = RelayAsync(output, input, remoteDecrypt, clientEncrypt, false, connection.Token);
            await Task.WhenAny(upload, download);
            await connection.CancelAsync();
            try { await Task.WhenAll(upload, download); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or System.Net.WebSockets.WebSocketException) { }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Net.WebSockets.WebSocketException or HttpRequestException)
        { if (!stop.IsCancellationRequested && ex is not OperationCanceledException) Error?.Invoke("Telegram Desktop: " + ex.Message); }
        finally { clients.TryRemove(id, out _); client.Dispose(); slots.Release(); }
    }
    private async Task RelayAsync(Stream source, Stream target, AesCounter decrypt, AesCounter encrypt, bool upload, CancellationToken token)
    {
        var buffer = new byte[32768];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            await target.WriteAsync(encrypt.Apply(decrypt.Apply(buffer[..count])), token);
            if (upload) Interlocked.Add(ref sent, count); else Interlocked.Add(ref received, count);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); listener.Stop(); foreach (var client in clients.Values) client.Dispose();
    }
}
