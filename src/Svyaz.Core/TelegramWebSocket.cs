using System.Net.WebSockets;

namespace Svyaz.Core;

public static class TelegramWebSocket
{
    public static async ValueTask<Stream> ConnectAsync(int dc, AppConfig config, CancellationToken token)
    {
        var name = Math.Abs(dc) switch { 1 => "pluto", 2 => "venus", 3 => "aurora", 4 => "vesta", 5 => "flora", _ => throw new IOException("Неизвестный DC Telegram.") };
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = (context, cancellation) => TelegramRoute.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, config, cancellation),
            ConnectTimeout = TimeSpan.FromSeconds(12)
        };
        var http = new HttpMessageInvoker(handler);
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("binary");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await socket.ConnectAsync(new Uri($"wss://{name}{(dc < 0 ? "-1" : "")}.web.telegram.org/apiws"), http, token);
            return new WebSocketStream(socket, http);
        }
        catch { socket.Dispose(); http.Dispose(); throw; }
    }
}

public sealed class WebSocketStream(ClientWebSocket socket, IDisposable? owner = null) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return 0;
            if (result.MessageType != WebSocketMessageType.Binary) throw new IOException("Telegram вернул нетипичный WebSocket-пакет.");
            if (result.Count != 0) return result.Count;
        }
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        await socket.SendAsync(buffer, WebSocketMessageType.Binary, true, cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    { if (disposing) { socket.Abort(); socket.Dispose(); owner?.Dispose(); } base.Dispose(disposing); }
}
