using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Svyaz.Core;

public sealed record NetworkChoice(string Id, string Name, int Index, string Address);

public static class DirectNetwork
{
    public static NetworkChoice[] Choices() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(n =>
        {
            var properties = n.GetIPProperties();
            var address = properties.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            return address == null ? null : new NetworkChoice(n.Id, n.Name, properties.GetIPv4Properties().Index, address.Address.ToString());
        }).OfType<NetworkChoice>().ToArray();

    public static async ValueTask<Stream> ConnectAsync(string host, int port, string interfaceId, CancellationToken token)
    {
        if (interfaceId.Length == 0)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(host, port, token); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Выбор интерфейса поддерживается в Windows.");
        var choice = Choices().SingleOrDefault(n => n.Id == interfaceId)
            ?? throw new IOException("Выбранный сетевой интерфейс недоступен. Другой маршрут автоматически не используется.");
        var addresses = (await Dns.GetHostAddressesAsync(host, token)).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(4).ToArray();
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        Exception? last = null;
        foreach (var address in addresses)
        {
            token.ThrowIfCancellationRequested();
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Windows IP_UNICAST_IF (31): index is explicitly network byte order.
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(choice.Index));
                socket.Bind(new IPEndPoint(IPAddress.Parse(choice.Address), 0));
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                await socket.ConnectAsync(address, port, attempt.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            { socket.Dispose(); token.ThrowIfCancellationRequested(); last = ex; }
        }
        throw new IOException("Не удалось установить прямое TCP-соединение через выбранный интерфейс.", last);
    }
}
