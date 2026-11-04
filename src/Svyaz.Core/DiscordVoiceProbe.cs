using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Svyaz.Core;

public static class DiscordVoiceProbe
{
    public static IPEndPoint? FindRecentEndpoint()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "logs");
        return FindRecentEndpoint(directory);
    }

    internal static IPEndPoint? FindRecentEndpoint(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("discord-webrtc_*").OrderByDescending(f => f.LastWriteTimeUtc).Take(2))
        {
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(Math.Max(0, stream.Length - 8 * 1024 * 1024), SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var matches = Regex.Matches(reader.ReadToEnd(), @"Create connection to (\d{1,3}(?:\.\d{1,3}){3}):(\d{1,5}) with ssrc:", RegexOptions.CultureInvariant);
                if (matches.Count == 0) continue;
                var last = matches[^1];
                if (IPAddress.TryParse(last.Groups[1].Value, out var address) && int.TryParse(last.Groups[2].Value, out var port)
                    && (port is >= 19294 and <= 19344 or >= 50000 and <= 65535)) return new(address, port);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static async Task<NativeProbeResult> CheckRecentAsync(string interfaceId, CancellationToken token)
    {
        var endpoint = await Task.Run(FindRecentEndpoint, token).ConfigureAwait(false);
        return endpoint == null ? new("Discord UDP", false, "В журнале нет недавнего голосового сервера. Сначала проверьте канал вручную.", 0, DateTimeOffset.Now)
            : await CheckAsync(endpoint, interfaceId, token).ConfigureAwait(false);
    }

    // An unauthed discovery packet with a new random SSRC. No token, voice
    // channel join, microphone access or audio is used or read.
    public static async Task<NativeProbeResult> CheckAsync(IPEndPoint endpoint, string interfaceId, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            if (endpoint.AddressFamily != AddressFamily.InterNetwork) throw new IOException("Для этой проверки требуется IPv4.");
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (interfaceId.Length != 0)
            {
                var choice = DirectNetwork.Choices().SingleOrDefault(c => c.Id == interfaceId) ?? throw new IOException("Выбранный интерфейс недоступен.");
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(choice.Index));
                socket.Bind(new IPEndPoint(IPAddress.Parse(choice.Address), 0));
            }
            socket.Connect(endpoint);
            var request = new byte[74];
            BinaryPrimitives.WriteUInt16BigEndian(request, 1);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 70);
            RandomNumberGenerator.Fill(request.AsSpan(4, 4));
            await socket.SendAsync(request, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            var response = new byte[256];
            var length = await socket.ReceiveAsync(response, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            if (length != 74 || BinaryPrimitives.ReadUInt16BigEndian(response) != 2 || BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2)) != 70
                || !response.AsSpan(4, 4).SequenceEqual(request.AsSpan(4, 4))) throw new IOException("Некорректный ответ UDP IP discovery.");
            return Result(true, "UDP IP discovery: корректный ответ. Аудио и маршрут самого клиента не проверены.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Result(false, "Нет ответа UDP от последнего сервера. Это не проверка слышимости в канале."); }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.NetworkReset or SocketError.ConnectionReset)
        { return Result(false, "Сетевой сброс UDP. Выключите экспериментальную обработку UDP и повторите проверку."); }
        catch (Exception ex) when (ex is SocketException or IOException)
        { return Result(false, "UDP: " + ex.Message); }

        NativeProbeResult Result(bool healthy, string detail) => new("Discord UDP", healthy, detail, watch.ElapsedMilliseconds, DateTimeOffset.Now);
    }
}
