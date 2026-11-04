using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace Svyaz.Core;

public static class DiscordProbe
{
    public static async Task<NativeProbeResult> CheckAsync(Uri localProxy, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.Proxy = new WebProxy(localProxy);
            await socket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), deadline.Token);
            var buffer = new byte[8192];
            var length = 0;
            WebSocketReceiveResult packet;
            do
            {
                if (length == buffer.Length) throw new IOException("Слишком большой ответ gateway.");
                packet = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), deadline.Token);
                length += packet.Count;
            } while (!packet.EndOfMessage);
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (json.RootElement.GetProperty("op").GetInt32() != 10) throw new IOException("Не получен gateway Hello.");
            // No Identify, token, account access, joins, messages or microphone.
            socket.Abort();
            return new("Discord Desktop", true, "Gateway WebSocket: получен Hello. Голос и вход не проверялись.", watch.ElapsedMilliseconds, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new("Discord Desktop", false, "Тайм-аут Gateway WebSocket", watch.ElapsedMilliseconds, DateTimeOffset.Now); }
        catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or InvalidOperationException)
        { return new("Discord Desktop", false, "Gateway: " + ex.Message, watch.ElapsedMilliseconds, DateTimeOffset.Now); }
    }
}
