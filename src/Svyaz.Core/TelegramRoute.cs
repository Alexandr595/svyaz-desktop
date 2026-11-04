using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;

namespace Svyaz.Core;

public static class TelegramRoute
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> FailedUntil = new();
    // Official ranges: https://core.telegram.org/resources/cidr.txt (2026-11-02).
    private static readonly IPNetwork[] Networks = new[]
    {
        "91.108.56.0/22", "91.108.4.0/22", "91.108.8.0/22", "91.108.16.0/22", "91.108.12.0/22",
        "149.154.160.0/20", "91.105.192.0/23", "91.108.20.0/22", "185.76.151.0/24",
        "2001:b28:f23d::/48", "2001:b28:f23f::/48", "2001:67c:4e8::/48", "2001:b28:f23c::/48", "2a0a:f280::/32"
    }.Select(IPNetwork.Parse).ToArray();

    public static bool IsTelegram(string host) => host.Equals("telegram.org", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".telegram.org", StringComparison.OrdinalIgnoreCase) || host.Equals("t.me", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("telegram.me", StringComparison.OrdinalIgnoreCase) || host.Equals("telesco.pe", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("tdesktop.com", StringComparison.OrdinalIgnoreCase) || host.Equals("telegram.dog", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && Networks.Any(n => n.Contains(address));

    public static bool IsChatGpt(string host) => ServiceCatalog.Presets[0].Domains.Any(domain =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    public static Uri? ParseProxy(string value)
    {
        if (value.Length == 0) return null;
        if (!Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "socks5://" + value, UriKind.Absolute, out var uri) ||
            uri.Scheme != "socks5" || uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Прокси Telegram: socks5://адрес:порт, без логина и пароля.");
        return uri;
    }

    public static async ValueTask<Stream> ConnectAsync(string host, int port, AppConfig config, CancellationToken token)
    {
        var primary = IsTelegram(host) ? config.TelegramProxy : IsChatGpt(host) ? config.ChatGptProxy : "";
        var backups = IsTelegram(host) ? config.TelegramProxyBackups : config.ChatGptProxyBackups;
        if (primary.Length == 0) return await DirectNetwork.ConnectAsync(host, port, config.InterfaceId, token);
        var proxies = new[] { primary }.Concat(backups).Where(s => s.Length != 0).Distinct().ToArray();
        var available = proxies.Where(p => !FailedUntil.TryGetValue(p, out var until) || until <= DateTimeOffset.UtcNow).ToArray();
        if (available.Length == 0) available = proxies;
        Exception? last = null;
        foreach (var value in available)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var stream = await ViaSocksAsync(ParseProxy(value)!, host, port, config.InterfaceId, attempt.Token);
                FailedUntil.TryRemove(value, out _);
                return stream;
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                token.ThrowIfCancellationRequested(); last = ex;
                FailedUntil[value] = DateTimeOffset.UtcNow.AddSeconds(30);
            }
        }
        throw new IOException("Все настроенные прокси для этого сервиса недоступны.", last);
    }

    public static async ValueTask<Stream> ViaSocksAsync(Uri proxy, string host, int port, string interfaceId, CancellationToken token)
    {
        var stream = await DirectNetwork.ConnectAsync(proxy.IdnHost, proxy.Port, interfaceId, token);
        try
        {
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
            var response = new byte[2]; await stream.ReadExactlyAsync(response, token);
            if (response[0] != 5 || response[1] != 0) throw new IOException("SOCKS5-прокси требует авторизацию или отклонил соединение.");
            byte[] address;
            byte type;
            if (IPAddress.TryParse(host, out var ip))
            {
                type = ip.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
                address = ip.GetAddressBytes();
            }
            else
            {
                type = 3; var domain = Encoding.ASCII.GetBytes(new IdnMapping().GetAscii(host));
                if (domain.Length is < 1 or > 253) throw new IOException("Слишком длинный домен.");
                address = new byte[] { (byte)domain.Length }.Concat(domain).ToArray();
            }
            await stream.WriteAsync(new byte[] { 5, 1, 0, type }.Concat(address).Concat(new byte[] { (byte)(port >> 8), (byte)port }).ToArray(), token);
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
            if (header[0] != 5 || header[1] != 0 || header[2] != 0) throw new IOException("SOCKS5-прокси не открыл соединение: " + header[1]);
            var length = header[3] switch { 1 => 4, 4 => 16, 3 => 0, _ => throw new IOException("Некорректный ответ SOCKS5.") };
            if (length == 0) { var size = new byte[1]; await stream.ReadExactlyAsync(size, token); length = size[0]; }
            await stream.ReadExactlyAsync(new byte[length + 2], token);
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
}
