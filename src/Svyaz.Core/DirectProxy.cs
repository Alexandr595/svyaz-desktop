using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Svyaz.Core;

// A loopback CONNECT/SOCKS5 relay. TLS remains end-to-end; no certificate is
// installed, and every outgoing connection uses the selected interface.
public sealed class DirectProxy(string interfaceId,
    Func<string, int, CancellationToken, ValueTask<Stream>>? connector = null, int preferredPort = 0) : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, preferredPort);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<int, TcpClient> clients = new();
    private readonly SemaphoreSlim slots = new(128);
    private int serial;
    private int disposed;
    private long sent;
    private long received;
    private long total;
    public string? PacScript { get; set; }
    public ProxyTraffic Traffic => new(clients.Count, Interlocked.Read(ref total), Interlocked.Read(ref sent), Interlocked.Read(ref received));
    public int Port { get; private set; }
    public bool IsRunning => Port != 0 && Volatile.Read(ref disposed) == 0;
    public event Action<string>? Error;

    public void Start()
    {
        if (Port != 0) throw new InvalidOperationException("Локальный прокси уже запущен.");
        listener.Start(128);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref serial);
                clients[id] = client;
                Interlocked.Increment(ref total);
                _ = ServeAsync(id, client);
            }
        }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception ex) { Error?.Invoke("Приём локальных соединений: " + ex.Message); }
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
            var first = new byte[1];
            await input.ReadExactlyAsync(first, handshake.Token);
            string host;
            int port;
            var socks = first[0] == 5;
            if (socks)
            {
                await input.ReadExactlyAsync(first, handshake.Token);
                var methods = new byte[first[0]];
                await input.ReadExactlyAsync(methods, handshake.Token);
                if (!methods.Contains((byte)0)) { await input.WriteAsync(new byte[] { 5, 255 }, handshake.Token); return; }
                await input.WriteAsync(new byte[] { 5, 0 }, handshake.Token);
                var header = new byte[4];
                await input.ReadExactlyAsync(header, handshake.Token);
                if (header[0] != 5 || header[1] != 1 || header[2] != 0) throw new IOException("Поддерживается SOCKS5 CONNECT.");
                switch (header[3])
                {
                    case 1:
                        var ipv4 = new byte[4]; await input.ReadExactlyAsync(ipv4, handshake.Token); host = new IPAddress(ipv4).ToString(); break;
                    case 3:
                        await input.ReadExactlyAsync(first, handshake.Token);
                        var name = new byte[first[0]]; await input.ReadExactlyAsync(name, handshake.Token); host = Encoding.ASCII.GetString(name); break;
                    case 4:
                        var ipv6 = new byte[16]; await input.ReadExactlyAsync(ipv6, handshake.Token); host = new IPAddress(ipv6).ToString(); break;
                    default: throw new IOException("Неизвестный тип адреса SOCKS5.");
                }
                var portBytes = new byte[2]; await input.ReadExactlyAsync(portBytes, handshake.Token);
                port = (portBytes[0] << 8) | portBytes[1];
            }
            else
            {
                var header = new List<byte>(1024) { first[0] };
                while (header.Count < 16384)
                {
                    await input.ReadExactlyAsync(first, handshake.Token); header.Add(first[0]);
                    var n = header.Count;
                    if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
                }
                if (header.Count >= 16384) throw new IOException("Слишком большой заголовок прокси.");
                var request = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", 2)[0].Split(' ');
                if (request.Length == 3 && request[0] == "GET" && (request[1] == "/proxy.pac" || request[1].StartsWith("/proxy.pac?", StringComparison.Ordinal)) && PacScript != null)
                {
                    var body = Encoding.UTF8.GetBytes(PacScript);
                    await input.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/x-ns-proxy-autoconfig\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), handshake.Token);
                    await input.WriteAsync(body, handshake.Token);
                    return;
                }
                if (request.Length != 3 || request[0] != "CONNECT" || !Uri.TryCreate("https://" + request[1], UriKind.Absolute, out var authority) ||
                    authority.UserInfo.Length != 0 || authority.AbsolutePath != "/" || authority.Query.Length != 0 || authority.Fragment.Length != 0)
                {
                    await input.WriteAsync("HTTP/1.1 405 Method Not Allowed\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), handshake.Token);
                    return;
                }
                host = authority.IdnHost; port = authority.Port;
            }
            if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) throw new IOException("Некорректный адрес прокси.");
            await using var output = await (connector?.Invoke(host, port, handshake.Token) ?? DirectNetwork.ConnectAsync(host, port, interfaceId, handshake.Token));
            if (socks) await input.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, handshake.Token);
            else await input.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), handshake.Token);
            var upload = CopyAsync(input, output, true, connection.Token);
            var download = CopyAsync(output, input, false, connection.Token);
            await Task.WhenAny(upload, download);
            await connection.CancelAsync();
            try { await Task.WhenAll(upload, download); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        { if (!stop.IsCancellationRequested && ex is not OperationCanceledException) Error?.Invoke(ex.Message); }
        finally { clients.TryRemove(id, out _); client.Dispose(); slots.Release(); }
    }

    private async Task CopyAsync(Stream input, Stream output, bool upload, CancellationToken token)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(32768);
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                if (upload) Interlocked.Add(ref sent, count); else Interlocked.Add(ref received, count);
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); listener.Stop();
        foreach (var client in clients.Values) client.Dispose();
    }
}

public sealed record ProxyTraffic(int ActiveConnections, long TotalConnections, long SentBytes, long ReceivedBytes);
