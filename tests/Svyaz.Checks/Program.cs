using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;
using System.Buffers.Binary;
using Svyaz.Core;

if (args.FirstOrDefault() == "--sleep") { await Task.Delay(TimeSpan.FromMinutes(2)); return 0; }
if (args.FirstOrDefault() == "--job-owner")
{
    using var job = new WindowsJob();
    using var child = Child("--sleep");
    job.Assign(child);
    File.WriteAllText(args[1], child.Id.ToString());
    await Task.Delay(TimeSpan.FromMinutes(2));
    return 0;
}
if (args.FirstOrDefault() == "--diagnose")
{
    var config = args.Length > 1 ? AppConfig.Load(args[1]) : new AppConfig().Validate();
    var report = await new HttpsProbeService().CheckAsync(config, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
if (args.FirstOrDefault() == "--system-proxy-state")
{
    Console.WriteLine(JsonSerializer.Serialize(new WindowsInternetProxySettings().Read())); return 0;
}
if (args.FirstOrDefault() == "--proxy-owner")
{
    using var proxy = new DirectProxy(""); proxy.Start();
    proxy.PacScript = DesktopPac.Create(["svyaz-proxy-probe.invalid"], proxy.Port);
    using var lease = new DesktopProxyLease(args[1], proxy.Port, new WindowsInternetProxySettings());
    var start = new ProcessStartInfo(args[2]) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--proxy-watch"); start.ArgumentList.Add(Environment.ProcessId.ToString());
    Process.Start(start)?.Dispose();
    File.WriteAllText(args[3], "ready");
    await Task.Delay(TimeSpan.FromMinutes(1)); return 0;
}
if (args.FirstOrDefault() == "--proxy-watch-smoke")
{
    var executable = Path.GetFullPath(args[1]);
    var directory = Path.Combine(Path.GetDirectoryName(executable)!, "data");
    var signal = Path.GetFullPath("artifacts/proxy-owner-ready-" + Guid.NewGuid() + ".txt");
    var store = new WindowsInternetProxySettings(); var before = store.Read();
    using var owner = Child("--proxy-owner", directory, executable, signal);
    try
    {
        var readyBy = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(signal) && !owner.HasExited && DateTime.UtcNow < readyBy) await Task.Delay(100);
        Assert(File.Exists(signal) && store.Read().AutoConfigUrl != before.AutoConfigUrl);
        owner.Kill(); await owner.WaitForExitAsync();
        var restoreBy = DateTime.UtcNow.AddSeconds(10);
        while (store.Read() != before && DateTime.UtcNow < restoreBy) await Task.Delay(100);
        Assert(store.Read() == before && !File.Exists(Path.Combine(directory, "desktop-proxy-backup.json")));
        Console.WriteLine("PASS Actual watchdog restored Windows PAC after abrupt owner termination.");
    }
    finally
    {
        if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); }
        DesktopProxyLease.Restore(directory, store, owner.Id);
        File.Delete(signal);
    }
    return 0;
}
if (args.FirstOrDefault() == "--desktop-proxy-smoke")
{
    var store = new WindowsInternetProxySettings(); var before = store.Read();
    var routed = false; var unexpected = false;
    using var proxy = new DirectProxy("", (host, _, _) =>
    {
        if (host == "svyaz-proxy-probe.invalid") routed = true; else unexpected = true;
        throw new IOException("Intentional PAC routing probe.");
    });
    proxy.Start(); proxy.PacScript = DesktopPac.Create(["svyaz-proxy-probe.invalid"], proxy.Port);
    using (var lease = new DesktopProxyLease("artifacts/system-pac-smoke", proxy.Port, store))
    {
        Console.WriteLine("PAC registered via WinINet; before flags=" + before.ConnectionFlags + ", after=" + store.Read().ConnectionFlags);
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("tests/system-pac-check.cjs");
        using var child = Process.Start(start)!;
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
        Console.WriteLine($"Routed selected host: {routed}; unrelated host reached proxy: {unexpected}; browser exit: {child.ExitCode}");
        Assert(routed && !unexpected && child.ExitCode == 0);
    }
    Assert(store.Read() == before);
    Console.WriteLine("Original Windows proxy flags and values restored exactly.");
    return 0;
}
if (args.FirstOrDefault() == "--interfaces")
{
    Console.WriteLine(JsonSerializer.Serialize(DirectNetwork.Choices(), new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
if (args.FirstOrDefault() == "--telegram-obfuscated-check")
{
    var config = AppConfig.Load(args[1]);
    var result = await TelegramProbe.CheckAsync(config, CancellationToken.None, args.Length > 2 ? args[2] : "149.154.167.51");
    Console.WriteLine(JsonSerializer.Serialize(result));
    return result.Healthy ? 0 : 1;
}
if (args.FirstOrDefault() == "--telegram-ws-check")
{
    var config = AppConfig.Load(args[1]);
    var result = await TelegramProbe.CheckAsync(config, CancellationToken.None, connector: (_, _, token) => TelegramWebSocket.ConnectAsync(2, config, token));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return result.Healthy ? 0 : 1;
}
if (args.FirstOrDefault() == "--telegram-bridge-check")
{
    var config = AppConfig.Load(args[1]) with { TelegramLocalPort = 18082 };
    using var bridge = new TelegramDesktopBridge(config, "artifacts/telegram-bridge-check");
    bridge.Error += Console.Error.WriteLine;
    bridge.Start();
    var result = await TelegramProbe.CheckAsync(config, CancellationToken.None,
        connector: (_, _, token) => DirectNetwork.ConnectAsync("127.0.0.1", bridge.Port, "", token), proxySecret: bridge.Secret);
    Console.WriteLine(JsonSerializer.Serialize(result));
    return result.Healthy ? 0 : 1;
}
if (args.FirstOrDefault() == "--native-client-check")
{
    var config = AppConfig.Load(args[1]);
    var secret = Convert.FromHexString(File.ReadAllText(Path.Combine(Path.GetDirectoryName(args[1])!, "telegram-local-secret.txt")).Trim());
    var results = await Task.WhenAll(
        TelegramProbe.CheckAsync(config, CancellationToken.None, connector: (_, _, token) => DirectNetwork.ConnectAsync("127.0.0.1", config.TelegramLocalPort, "", token), proxySecret: secret),
        DiscordProbe.CheckAsync(new Uri($"http://127.0.0.1:{config.LocalProxyPort}"), CancellationToken.None));
    Console.WriteLine(JsonSerializer.Serialize(results));
    return results.All(r => r.Healthy) ? 0 : 1;
}
if (args.FirstOrDefault() is "--check-proxies" or "--check-openai-proxies" or "--check-native-proxies")
{
    var config = AppConfig.Load(args[2]);
    using var limiter = new SemaphoreSlim(4);
    var candidates = File.ReadAllLines(args[1]).Where(s => !string.IsNullOrWhiteSpace(s)).Take(32).ToArray();
    var checks = await Task.WhenAll(candidates.Select(async candidate =>
    {
        await limiter.WaitAsync();
        try
        {
            if (args[0] == "--check-native-proxies")
            {
                var native = await TelegramProbe.CheckAsync(config with { TelegramProxy = candidate, TelegramProxyBackups = [] }, CancellationToken.None);
                return new { Proxy = candidate, native.Healthy, StatusCode = (int?)null, native.ElapsedMs, native.Detail };
            }
            var openai = args[0] == "--check-openai-proxies";
            var result = await HttpsProbeService.ProbeAsync(openai ? "https://chatgpt.com/" : "https://web.telegram.org/", false, 8,
                CancellationToken.None, config.InterfaceId, openai ? "" : candidate, [], openai ? candidate : "", []);
            return new { Proxy = candidate, result.Healthy, result.StatusCode, result.ElapsedMs, result.Detail };
        }
        finally { limiter.Release(); }
    }));
    Console.WriteLine(JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
if (args.FirstOrDefault() == "--telegram-check")
{
    var config = AppConfig.Load(args[1]);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    var target = args.Length > 2 ? args[2] : "149.154.167.51";
    await using var stream = await TelegramRoute.ConnectAsync(target, 443, config, timeout.Token);
    var nonce = RandomNumberGenerator.GetBytes(16);
    var payload = new byte[40];
    var now = DateTimeOffset.UtcNow;
    var messageId = ((ulong)now.ToUnixTimeSeconds() << 32) | ((ulong)(now.Millisecond * (1L << 32) / 1000) & ~3UL);
    if ((uint)messageId == 0) messageId += 4;
    BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), messageId);
    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), 20);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), 0xbe7e8ef1);
    nonce.CopyTo(payload, 24);
    await stream.WriteAsync(new byte[] { 0xef, 10 }.Concat(payload).ToArray(), timeout.Token);
    var size = new byte[1]; await stream.ReadExactlyAsync(size, timeout.Token);
    var words = (int)size[0];
    if (words == 127)
    {
        var extended = new byte[3]; await stream.ReadExactlyAsync(extended, timeout.Token);
        words = extended[0] | extended[1] << 8 | extended[2] << 16;
    }
    if (words is < 15 or > 1024) throw new IOException("Unexpected MTProto response length");
    var response = new byte[words * 4]; await stream.ReadExactlyAsync(response, timeout.Token);
    if (BinaryPrimitives.ReadUInt64LittleEndian(response) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(20)) != 0x05162463 ||
        !response.AsSpan(24, 16).SequenceEqual(nonce)) throw new IOException("Unexpected MTProto resPQ response");
    Console.WriteLine("MTProto resPQ received; nonce matches. No account login, auth key generation or messages sent.");
    return 0;
}
if (args.FirstOrDefault() == "--proxy-browser-trials")
{
    var config = AppConfig.Load(args[1]);
    var candidates = new[] { "socks5://47.90.162.114:5555", "socks5://78.110.155.229:1080", "socks5://183.80.59.62:1083", "socks5://42.115.72.145:1105" };
    var proxies = new List<DirectProxy>();
    var ports = new List<object>();
    try
    {
        foreach (var candidate in candidates)
        {
            var route = config with { ChatGptProxy = candidate, ChatGptProxyBackups = [] };
            var proxy = new DirectProxy(config.InterfaceId, (host, port, token) => TelegramRoute.ConnectAsync(host, port, route, token));
            proxy.Start(); proxies.Add(proxy); ports.Add(new { upstream = candidate, server = "http://127.0.0.1:" + proxy.Port });
        }
        File.WriteAllText("artifacts/proxy-browser-trials.json", JsonSerializer.Serialize(ports));
        Console.WriteLine("Proxy browser trial listeners ready (180 seconds maximum).");
        await Task.Delay(TimeSpan.FromSeconds(180));
    }
    finally { foreach (var proxy in proxies) proxy.Dispose(); }
    return 0;
}

var failed = 0;
await Check("AES-256 CTR matches the NIST vector across partial reads", () =>
{
    var key = Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
    var iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
    var plain = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");
    var expected = Convert.FromHexString("601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c52b0930daa23de94ce87017ba2d84988ddfc9c58db67aada613c2dd08457941a6");
    using var ctr = new AesCounter(key, iv);
    var output = ctr.Apply(plain[..3]).Concat(ctr.Apply(plain[3..31])).Concat(ctr.Apply(plain[31..])).ToArray();
    Assert(output.SequenceEqual(expected));
    return Task.CompletedTask;
});
await Check("Desktop PAC lease restores exact previous values", () =>
{
    var folder = Path.Combine("artifacts", "pac-test-" + Guid.NewGuid());
    var before = new InternetProxyState(null, null);
    var store = new FakeInternetProxy { State = before };
    try
    {
        using (var lease = new DesktopProxyLease(folder, 18080, store))
        { Assert(store.State.AutoConfigUrl == lease.Url && store.State.ProxyEnable == 0); }
        Assert(store.State == before && !File.Exists(Path.Combine(folder, "desktop-proxy-backup.json")));
    }
    finally { Directory.Delete(folder, true); }
    return Task.CompletedTask;
});
await Check("Desktop PAC lease restores Windows auto-detect flags", () =>
{
    var folder = Path.Combine("artifacts", "pac-test-" + Guid.NewGuid());
    var before = new InternetProxyState(null, 0, 9);
    var store = new FakeInternetProxy { State = before };
    try
    {
        using (var lease = new DesktopProxyLease(folder, 18080, store)) Assert(store.State.ConnectionFlags == 13);
        Assert(store.State == before);
    }
    finally { Directory.Delete(folder, true); }
    return Task.CompletedTask;
});
await Check("Desktop PAC restore respects later changes by other apps", () =>
{
    var folder = Path.Combine("artifacts", "pac-test-" + Guid.NewGuid());
    var store = new FakeInternetProxy { State = new InternetProxyState(null, 0) };
    try
    {
        using (var lease = new DesktopProxyLease(folder, 18080, store))
            store.State = new InternetProxyState("https://example.com/another.pac", 1);
        Assert(store.State == new InternetProxyState("https://example.com/another.pac", 1));
    }
    finally { Directory.Delete(folder, true); }
    return Task.CompletedTask;
});
await Check("Desktop PAC refuses to replace an existing system proxy", () =>
{
    var folder = Path.Combine("artifacts", "pac-test-" + Guid.NewGuid());
    var before = new InternetProxyState(null, 1);
    var store = new FakeInternetProxy { State = before };
    try
    {
        Throws(() => new DesktopProxyLease(folder, 18080, store));
        Assert(store.State == before && !File.Exists(Path.Combine(folder, "desktop-proxy-backup.json")));
    }
    finally { Directory.Delete(folder, true); }
    return Task.CompletedTask;
});
await Check("Crash recovery is scoped to the proxy lease owner", () =>
{
    var folder = Path.Combine("artifacts", "pac-test-" + Guid.NewGuid());
    var before = new InternetProxyState(null, 0);
    var store = new FakeInternetProxy { State = before };
    try
    {
        using var lease = new DesktopProxyLease(folder, 18080, store);
        Assert(!DesktopProxyLease.Restore(folder, store, Environment.ProcessId + 1));
        Assert(store.State.AutoConfigUrl == lease.Url);
        Assert(DesktopProxyLease.Restore(folder, store, Environment.ProcessId) && store.State == before);
    }
    finally { Directory.Delete(folder, true); }
    return Task.CompletedTask;
});
await Check("Loopback PAC endpoint serves rules without forwarding an HTTP request", async () =>
{
    var forwarded = false;
    using var proxy = new DirectProxy("", (_, _, _) => { forwarded = true; throw new IOException("Unexpected forwarding"); });
    proxy.Start(); proxy.PacScript = DesktopPac.Create(["discord.com", "youtube.com"], proxy.Port);
    using var handler = new SocketsHttpHandler { UseProxy = false };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    using var response = await client.GetAsync($"http://127.0.0.1:{proxy.Port}/proxy.pac?session=test");
    var pac = await response.Content.ReadAsStringAsync();
    Assert(response.IsSuccessStatusCode && !forwarded && pac.Contains("discord.com") && pac.Contains("return \"DIRECT\""));
    Assert(response.Content.Headers.ContentType?.MediaType == "application/x-ns-proxy-autoconfig");
});
await Check("External proxy failure cannot roll back direct DPI traffic", () =>
{
    var baseline = Results(true).Append(new ProbeResult("https://web.telegram.org/", false, true, 200, 20, "HTTP 200", DateTimeOffset.Now) { ViaExternalProxy = true }).ToArray();
    var policy = new HealthPolicy(baseline);
    var after = Results(true).Append(new ProbeResult("https://web.telegram.org/", false, false, null, 20, "Proxy failed", DateTimeOffset.Now) { ViaExternalProxy = true }).ToArray();
    Assert(policy.Observe(after) == null && policy.Observe(after) == null);
    return Task.CompletedTask;
});
await Check("Installation lease prevents duplicate instances and releases on close", () =>
{
    var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "lease-" + Guid.NewGuid());
    try
    {
        using (var first = InstanceLease.TryAcquire(directory))
        {
            Assert(first != null);
            using var second = InstanceLease.TryAcquire(directory);
            Assert(second == null);
        }
        using var released = InstanceLease.TryAcquire(directory);
        Assert(released != null);
    }
    finally { Directory.Delete(directory, true); }
    return Task.CompletedTask;
});
await Check("Persistent status reflects stopped engine and strips URL query secrets", () =>
{
    var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "journal-" + Guid.NewGuid());
    try
    {
        var journal = new SessionJournal(directory);
        journal.Append("test");
        journal.Status(SessionState.RolledBack, "control failed", false, true, 18080,
            [new ProbeResult("https://example.com/?token=secret", true, false, null, 20, "failed", DateTimeOffset.Now)]);
        var snapshot = File.ReadAllText(Path.Combine(directory, "status.json"));
        Assert(snapshot.Contains("RolledBack") && snapshot.Contains("\"engineRunning\": false") && !snapshot.Contains("secret"));
        Assert(File.ReadAllText(Path.Combine(directory, "session.log")).Contains("test"));
    }
    finally { Directory.Delete(directory, true); }
    return Task.CompletedTask;
});
await Check("HTTPS probe goes through the same CONNECT listener as the browser", async () =>
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=proxy-check.invalid", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var endpoint = (IPEndPoint)listener.LocalEndpoint;
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var forwarded = false;
    var server = Task.Run(async () =>
    {
        try
        {
            using var tcp = await listener.AcceptTcpClientAsync(cancel.Token);
            using var tls = new SslStream(tcp.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, cancel.Token);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException) { }
    });
    using var proxy = new DirectProxy("", async (host, port, token) =>
    {
        forwarded = host == "proxy-check.invalid" && port == 443;
        var tcp = new TcpClient(); await tcp.ConnectAsync(endpoint, token); return tcp.GetStream();
    });
    proxy.Start();
    var result = await HttpsProbeService.ProbeAsync("https://proxy-check.invalid/", false, 3, cancel.Token,
        localProxy: new Uri($"http://127.0.0.1:{proxy.Port}"));
    Assert(forwarded && !result.Healthy && result.Detail.Contains("TLS"));
    cancel.Cancel(); listener.Stop(); await server;
});
await Check("External proxy route is restricted to Telegram domains and official networks", () =>
{
    Assert(TelegramRoute.IsTelegram("web.telegram.org") && TelegramRoute.IsTelegram("149.154.167.99"));
    Assert(TelegramRoute.IsTelegram("91.108.4.10") && TelegramRoute.IsTelegram("t.me"));
    Assert(!TelegramRoute.IsTelegram("chatgpt.com") && !TelegramRoute.IsTelegram("telegram.org.example.com"));
    Assert(!TelegramRoute.IsTelegram("8.8.8.8") && !TelegramRoute.IsTelegram("149.155.167.99"));
    Assert(TelegramRoute.ParseProxy("socks5://127.0.0.1:1080")?.Port == 1080);
    Throws(() => TelegramRoute.ParseProxy("socks5://user:password@example.com:1080"));
    Throws(() => TelegramRoute.ParseProxy("http://example.com:1080"));
    return Task.CompletedTask;
});
await Check("Upstream SOCKS5 preserves target domain and encrypted tunnel payload", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var endpoint = (IPEndPoint)listener.LocalEndpoint;
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var server = Task.Run(async () =>
    {
        using var tcp = await listener.AcceptTcpClientAsync(cancel.Token);
        await using var stream = tcp.GetStream();
        var greeting = new byte[3]; await stream.ReadExactlyAsync(greeting, cancel.Token);
        Assert(greeting.SequenceEqual(new byte[] { 5, 1, 0 }));
        await stream.WriteAsync(new byte[] { 5, 0 }, cancel.Token);
        var header = new byte[5]; await stream.ReadExactlyAsync(header, cancel.Token);
        Assert(header[0] == 5 && header[1] == 1 && header[3] == 3);
        var nameAndPort = new byte[header[4] + 2]; await stream.ReadExactlyAsync(nameAndPort, cancel.Token);
        Assert(Encoding.ASCII.GetString(nameAndPort, 0, header[4]) == "web.telegram.org");
        Assert(nameAndPort[^2] == 1 && nameAndPort[^1] == 187);
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, cancel.Token);
        var payload = new byte[4]; await stream.ReadExactlyAsync(payload, cancel.Token);
        await stream.WriteAsync(payload, cancel.Token);
    });
    await using var client = await TelegramRoute.ViaSocksAsync(new Uri($"socks5://127.0.0.1:{endpoint.Port}"), "web.telegram.org", 443, "", cancel.Token);
    var bytes = new byte[] { 16, 3, 3, 255 }; await client.WriteAsync(bytes, cancel.Token);
    var reply = new byte[4]; await client.ReadExactlyAsync(reply, cancel.Token); Assert(reply.SequenceEqual(bytes));
    await server;
});
await Check("Unavailable selected interface cannot fall back to the VPN route", async () =>
{
    try
    {
        using var stream = await DirectNetwork.ConnectAsync("example.com", 443, "missing-interface", CancellationToken.None);
        throw new Exception("Unexpected fallback");
    }
    catch (IOException ex) { Assert(ex.Message.Contains("интерфейс")); }
});
await Check("CONNECT proxy preserves tunnel bytes and binds only loopback", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var endpoint = (IPEndPoint)listener.LocalEndpoint;
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var server = Task.Run(async () =>
    {
        using var socket = await listener.AcceptTcpClientAsync(cancel.Token);
        await using var stream = socket.GetStream();
        var bytes = new byte[7]; await stream.ReadExactlyAsync(bytes, cancel.Token);
        await stream.WriteAsync(bytes, cancel.Token);
    });
    using var proxy = new DirectProxy("test", async (host, port, token) =>
    {
        Assert(host == "test.example" && port == 443);
        var socket = new TcpClient(); await socket.ConnectAsync(endpoint, token); return socket.GetStream();
    });
    proxy.Start();
    using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxy.Port, cancel.Token);
    await using var input = client.GetStream();
    await input.WriteAsync("CONNECT test.example:443 HTTP/1.1\r\nHost: test.example\r\n\r\n"u8.ToArray(), cancel.Token);
    var expected = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
    var reply = new byte[expected.Length]; await input.ReadExactlyAsync(reply, cancel.Token); Assert(reply.SequenceEqual(expected));
    var payload = new byte[] { 0, 255, 16, 3, 3, 33, 42 }; await input.WriteAsync(payload, cancel.Token);
    var echoed = new byte[payload.Length]; await input.ReadExactlyAsync(echoed, cancel.Token); Assert(echoed.SequenceEqual(payload));
    await server;
});
await Check("SOCKS5 proxy supports domain CONNECT and does not alter bytes", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var endpoint = (IPEndPoint)listener.LocalEndpoint;
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var server = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(cancel.Token);
        await using var stream = client.GetStream(); var buffer = new byte[3]; await stream.ReadExactlyAsync(buffer, cancel.Token);
        await stream.WriteAsync(buffer, cancel.Token);
    });
    using var proxy = new DirectProxy("test", async (host, port, token) =>
    {
        Assert(host == "test.example" && port == 443);
        var socket = new TcpClient(); await socket.ConnectAsync(endpoint, token); return socket.GetStream();
    });
    proxy.Start();
    using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, proxy.Port, cancel.Token);
    await using var input = tcp.GetStream(); await input.WriteAsync(new byte[] { 5, 1, 0 }, cancel.Token);
    var method = new byte[2]; await input.ReadExactlyAsync(method, cancel.Token); Assert(method.SequenceEqual(new byte[] { 5, 0 }));
    var domain = Encoding.ASCII.GetBytes("test.example");
    await input.WriteAsync(new byte[] { 5, 1, 0, 3, (byte)domain.Length }.Concat(domain).Concat(new byte[] { 1, 187 }).ToArray(), cancel.Token);
    var accepted = new byte[10]; await input.ReadExactlyAsync(accepted, cancel.Token); Assert(accepted[1] == 0);
    await input.WriteAsync(new byte[] { 16, 3, 42 }, cancel.Token);
    var echoed = new byte[3]; await input.ReadExactlyAsync(echoed, cancel.Token); Assert(echoed.SequenceEqual(new byte[] { 16, 3, 42 }));
    await server;
});
await Check("Stopping the proxy closes existing client connections", async () =>
{
    using var proxy = new DirectProxy(""); proxy.Start();
    using var client = new TcpClient(); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await client.ConnectAsync(IPAddress.Loopback, proxy.Port, cancel.Token);
    await client.GetStream().WriteAsync(new byte[] { 5, 1, 0 }, cancel.Token);
    var method = new byte[2]; await client.GetStream().ReadExactlyAsync(method, cancel.Token);
    proxy.Dispose();
    var single = new byte[1];
    try { Assert(await client.GetStream().ReadAsync(single, cancel.Token) == 0); }
    catch (IOException) { }
});
await Check("Modern profiles always use host scope and cap TLS packet handling", () =>
{
    foreach (var profile in ZapretEngine.Profiles.Where(p => !p.TelegramSyn))
    {
        var arguments = ZapretEngine.Arguments(new AppConfig { Profile = profile.Id }, "hosts.txt", "exclude.txt");
        Assert(arguments.Contains("--hostlist=hosts.txt") && arguments.Contains("--hostlist-exclude=exclude.txt"));
        Assert(arguments.Contains("--payload=tls_client_hello") && arguments.Contains("--out-range=-d4"));
        Assert(arguments.Contains("--wf-udp-out=19294-19344,50000-65535"));
        Assert(arguments.Contains("--filter-l7=discord") && arguments.Contains("--payload=discord_ip_discovery"));
        Assert(!arguments.Contains("--filter-l7=stun") && !arguments.Contains("--filter-l7=discord,stun"));
        var withoutVoice = ZapretEngine.Arguments(new AppConfig { Profile = profile.Id, DiscordVoice = false }, "hosts.txt", "exclude.txt");
        Assert(!withoutVoice.Any(a => a.StartsWith("--wf-udp", StringComparison.Ordinal)));
    }
    return Task.CompletedTask;
});
await Check("Watchdog also protects an HTTP 403 response lost after start", () =>
{
    var baseline = Results(true).Select(r => r.IsCanary ? r : r with { Healthy = false, StatusCode = 403 }).ToArray();
    var policy = new HealthPolicy(baseline);
    var after = Results(true, false);
    Assert(policy.Observe(after) == null);
    Assert(policy.Observe(after) == "github.com");
    return Task.CompletedTask;
});
await Check("Trial flags losing HTTP 403 as a regression", async () =>
{
    using var engine = new FakeEngine();
    var probes = new FakeProbes(call => Results(true, false).Select(r =>
        call == 1 && !r.IsCanary ? r with { StatusCode = 403 } : r).ToArray());
    var result = await new ConnectionTrial(probes, engine) { StartupDelay = TimeSpan.Zero }
        .RunAsync(new AppConfig(), CancellationToken.None);
    Assert(result.Outcome == "Потерян ранее доступный HTTPS-ответ" && result.EngineStopped);
});
await Check("HTTP 403 is a response, not proof of a broken connection", () =>
{
    var response = new ProbeResult("https://chatgpt.com/", false, false, 403, 20, "HTTP 403", DateTimeOffset.Now);
    Assert(response.HttpsResponded && response.AccessNeedsBrowserCheck && !response.Healthy);
    return Task.CompletedTask;
});
await Check("Trial never calls an unchanged connection an improvement", async () =>
{
    using var engine = new FakeEngine();
    var trial = new ConnectionTrial(new FakeProbes(_ => Results(true, false)), engine) { StartupDelay = TimeSpan.Zero };
    var result = await trial.RunAsync(new AppConfig(), CancellationToken.None);
    Assert(result.Outcome == "Улучшение не подтверждено" && result.EngineStopped && engine.Starts == 1);
});
await Check("Trial confirms HTTP improvement only after a failed baseline", async () =>
{
    using var engine = new FakeEngine();
    var trial = new ConnectionTrial(new FakeProbes(call => Results(true, call > 1)), engine) { StartupDelay = TimeSpan.Zero };
    var result = await trial.RunAsync(new AppConfig(), CancellationToken.None);
    Assert(result.Outcome == "Есть улучшение HTTP" && result.EngineStopped);
});
await Check("Trial immediately stops on control regression", async () =>
{
    using var engine = new FakeEngine();
    var trial = new ConnectionTrial(new FakeProbes(call => Results(call == 1)), engine) { StartupDelay = TimeSpan.Zero };
    var result = await trial.RunAsync(new AppConfig(), CancellationToken.None);
    Assert(result.Outcome == "Сбой контрольного адреса" && result.EngineStopped);
});
await Check("Trial preserves the actual startup error and cleans up", async () =>
{
    using var engine = new FakeEngine { FailStart = true };
    var trial = new ConnectionTrial(new FakeProbes(_ => Results(true)), engine) { StartupDelay = TimeSpan.Zero };
    var result = await trial.RunAsync(new AppConfig(), CancellationToken.None);
    Assert(result.Error == "Startup failed" && result.EngineStopped);
});
await Check("Trial cancellation terminates an already started engine", async () =>
{
    using var engine = new FakeEngine();
    using var cancel = new CancellationTokenSource();
    var trial = new ConnectionTrial(new FakeProbes(call => { if (call == 2) cancel.Cancel(); return Results(true); }), engine) { StartupDelay = TimeSpan.Zero };
    var result = await trial.RunAsync(new AppConfig(), cancel.Token);
    Assert(result.Outcome == "Проверка отменена" && result.EngineStopped && engine.Starts == 1);
});
await Check("Service presets probe main pages rather than CDN apexes", () =>
{
    var config = new AppConfig().Validate();
    var urls = ServiceCatalog.ProbeUrls(config.Targets);
    Assert(config.Targets.Length <= 32 && urls.Length == 4);
    Assert(urls.Contains("https://chatgpt.com/") && urls.Contains("https://web.telegram.org/"));
    Assert(!urls.Any(u => u.Contains("googlevideo.com")));
    Assert(ServiceCatalog.ProbeUrls(["custom.example.net"]).Single() == "https://custom.example.net/");
    return Task.CompletedTask;
});
await Check("IDN, URL and duplicate normalization", () =>
{
    var cfg = new AppConfig { Targets = ["HTTPS://GITHUB.COM/", "github.com.", "пример.рф"] }.Validate();
    Assert(cfg.Targets.Length == 2 && cfg.Targets[1] == "xn--e1afmkfd.xn--p1ai");
    return Task.CompletedTask;
});
await Check("Reject injection, IP, wildcards and unsupported ports", () =>
{
    foreach (var bad in new[] { "--allow-no-sni", "*.example.com", "1.1.1.1", "https://a.example.com:8443", "x.example.com\n--dns-addr", "https://example.org/path", "a_.example.com", "com" })
        Throws(() => AppConfig.NormalizeDomain(bad));
    return Task.CompletedTask;
});
await Check("Reject canaries inside hostlist and empty control list", () =>
{
    Throws(() => new AppConfig { Targets = ["example.com"], Canaries = ["https://api.example.com/"] }.Validate());
    Throws(() => new AppConfig { Canaries = [] }.Validate());
    Throws(() => new AppConfig { Canaries = ["https://user:password@example.com/"] }.Validate());
    Throws(() => new AppConfig { Targets = null! }.Validate());
    return Task.CompletedTask;
});
await Check("Atomic configuration round-trip", () =>
{
    var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "svyaz-check-" + Guid.NewGuid());
    Directory.CreateDirectory(folder);
    try
    {
        var path = Path.Combine(folder, "settings.json");
        new AppConfig().Save(path);
        Assert(AppConfig.Load(path).Targets.SequenceEqual(new AppConfig().Targets));
        Assert(!File.Exists(path + ".tmp"));
    }
    finally { Directory.Delete(folder, true); }
    return Task.CompletedTask;
});
await Check("Profiles keep DNS, QUIC, RST and all-host switches out", () =>
{
    foreach (var profile in new[] { "gentle", "sni" })
    {
        var flags = GoodbyeEngine.Arguments(profile, "C:\\folder with spaces\\hosts.txt");
        Assert(flags.Contains("--blacklist") && flags[^1].Contains("folder with spaces"));
        Assert(!flags.Any(f => f is "-p" or "-q" or "--allow-no-sni" || f.StartsWith("--dns", StringComparison.Ordinal)));
    }
    Throws(() => GoodbyeEngine.Arguments("unknown", "hosts"));
    return Task.CompletedTask;
});
await Check("SHA-256 rejects changed or missing engine bundle", () =>
{
    var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "svyaz-hash-" + Guid.NewGuid());
    Directory.CreateDirectory(folder);
    try
    {
        Throws(() => GoodbyeEngine.VerifyBundle(folder));
        File.WriteAllText(Path.Combine(folder, "goodbyedpi.exe"), "not the official engine");
        Throws(() => GoodbyeEngine.VerifyBundle(folder));
    }
    finally { Directory.Delete(folder, true); }
    return Task.CompletedTask;
});
await Check("Unhealthy baseline never starts the engine", async () =>
{
    using var engine = new FakeEngine();
    var controller = new SessionController(new FakeProbes(_ => Results(false)), engine);
    var states = new List<SessionState>();
    controller.StateChanged += (state, _) => states.Add(state);
    await controller.RunAsync(new AppConfig(), CancellationToken.None);
    Assert(engine.Starts == 0 && states[^1] == SessionState.Faulted);
});
await Check("Two consecutive canary failures stop a running engine", async () =>
{
    using var engine = new FakeEngine();
    var controller = FastController(new FakeProbes(call => Results(call == 1)), engine);
    SessionState last = SessionState.Idle;
    controller.StateChanged += (state, _) => last = state;
    await controller.RunAsync(new AppConfig(), CancellationToken.None);
    Assert(engine.Starts == 1 && !engine.IsRunning && last == SessionState.RolledBack);
});
await Check("Intermittent failure resets; target failure does not roll back", async () =>
{
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    using var engine = new FakeEngine();
    var probes = new FakeProbes(call =>
    {
        if (call == 7) cancel.Cancel();
        return Results(call % 2 == 1, targetHealthy: false);
    });
    var controller = FastController(probes, engine);
    var rollback = false;
    controller.StateChanged += (state, _) => rollback |= state == SessionState.RolledBack;
    await controller.RunAsync(new AppConfig(), cancel.Token);
    Assert(probes.Calls == 7 && !rollback && !engine.IsRunning);
});
await Check("Missing canary results count as failures", () =>
{
    var policy = new HealthPolicy(Results(true));
    Assert(policy.Observe([]) == null);
    Assert(policy.Observe([]) != null);
    return Task.CompletedTask;
});
await Check("Cancellation during baseline prevents late engine start", async () =>
{
    using var cancel = new CancellationTokenSource();
    using var engine = new FakeEngine();
    var controller = FastController(new FakeProbes(_ => { cancel.Cancel(); return Results(true); }), engine);
    await controller.RunAsync(new AppConfig(), cancel.Token);
    Assert(engine.Starts == 0 && !engine.IsRunning);
});
await Check("Cancellation while running terminates the engine", async () =>
{
    using var cancel = new CancellationTokenSource();
    using var engine = new FakeEngine();
    var controller = FastController(new FakeProbes(_ => Results(true)), engine);
    controller.StateChanged += (state, _) => { if (state == SessionState.Running) cancel.Cancel(); };
    await controller.RunAsync(new AppConfig(), cancel.Token);
    Assert(engine.Starts == 1 && !engine.IsRunning);
});
await Check("Engine startup failure still performs cleanup", async () =>
{
    using var engine = new FakeEngine { FailStart = true };
    var controller = FastController(new FakeProbes(_ => Results(true)), engine);
    await controller.RunAsync(new AppConfig(), CancellationToken.None);
    Assert(engine.Stops == 1 && !engine.IsRunning);
});
await Check("Engine exit during verification is detected", async () =>
{
    using var engine = new FakeEngine();
    var probes = new FakeProbes(call => { if (call == 2) engine.Stop(); return Results(true); });
    var controller = FastController(probes, engine);
    SessionState last = SessionState.Idle;
    controller.StateChanged += (state, _) => last = state;
    await controller.RunAsync(new AppConfig(), CancellationToken.None);
    Assert(last == SessionState.Faulted && !engine.IsRunning);
});
await Check("Second simultaneous run is rejected", async () =>
{
    using var cancel = new CancellationTokenSource();
    using var engine = new FakeEngine();
    var gate = new TaskCompletionSource<IReadOnlyList<ProbeResult>>();
    var controller = new SessionController(new GatedProbes(gate.Task), engine);
    var run = controller.RunAsync(new AppConfig(), cancel.Token);
    try { await controller.RunAsync(new AppConfig(), cancel.Token); throw new Exception("Concurrent run was accepted"); }
    catch (InvalidOperationException) { }
    cancel.Cancel(); gate.SetResult(Results(true)); await run;
    Assert(engine.Starts == 0);
});
await Check("TLS certificates are verified (self-signed endpoint rejected)", async () =>
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var server = Task.Run(async () =>
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancel.Token);
            using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, cancel.Token);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException) { }
    });
    var result = await HttpsProbeService.ProbeAsync($"https://localhost:{port}/", true, 3, cancel.Token);
    Assert(!result.Healthy && result.Detail.Contains("TLS"));
    cancel.Cancel(); listener.Stop(); await server;
});
await Check("Stalled TLS connection respects timeout", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var pending = listener.AcceptTcpClientAsync();
    var watch = Stopwatch.StartNew();
    var probe = HttpsProbeService.ProbeAsync($"https://127.0.0.1:{port}/", true, 1, CancellationToken.None);
    using var connection = await pending.WaitAsync(TimeSpan.FromSeconds(5));
    var result = await probe;
    Assert(!result.Healthy && result.Detail.Contains("Тайм-аут") && watch.Elapsed < TimeSpan.FromSeconds(4));
});
if (OperatingSystem.IsWindows())
{
    await Check("Job close terminates its child process", async () =>
    {
        using var child = Child("--sleep");
        using (var job = new WindowsJob()) { job.Assign(child); Assert(!child.HasExited); }
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(child.HasExited);
    });
    await Check("Abrupt parent death also terminates the job child", async () =>
    {
        Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "artifacts"));
        var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "svyaz-owner-" + Guid.NewGuid() + ".txt");
        using var owner = Child("--job-owner", path);
        Process? child = null;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!File.Exists(path) && DateTime.UtcNow < deadline) await Task.Delay(25);
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(path)));
            owner.Kill(); await owner.WaitForExitAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert(child.HasExited);
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(entireProcessTree: true);
            if (child != null) { if (!child.HasExited) child.Kill(); child.Dispose(); }
            if (File.Exists(path)) File.Delete(path);
        }
    });
}
Console.WriteLine(failed == 0 ? "All checks passed." : $"{failed} checks failed.");
return failed == 0 ? 0 : 1;

async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static void Throws(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException) { return; }
    throw new Exception("Expected validation to fail");
}
static SessionController FastController(IProbeService probes, IEngine engine) => new(probes, engine)
{ InitialDelay = TimeSpan.Zero, IntervalOverride = TimeSpan.FromMilliseconds(1) };
static IReadOnlyList<ProbeResult> Results(bool canaryHealthy, bool targetHealthy = true) =>
[
    new("https://example.com/", true, canaryHealthy, canaryHealthy ? 200 : null, 15, "test", DateTimeOffset.Now),
    new("https://www.microsoft.com/", true, true, 200, 15, "test", DateTimeOffset.Now),
    new("https://github.com/", false, targetHealthy, targetHealthy ? 200 : null, 15, "test", DateTimeOffset.Now)
];
static Process Child(params string[] arguments)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(typeof(FakeEngine).Assembly.Location);
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    return Process.Start(info)!;
}

sealed class FakeProbes(Func<int, IReadOnlyList<ProbeResult>> get) : IProbeService
{
    public int Calls { get; private set; }
    public Task<IReadOnlyList<ProbeResult>> CheckAsync(AppConfig config, CancellationToken token) => Task.FromResult(get(++Calls));
}
sealed class GatedProbes(Task<IReadOnlyList<ProbeResult>> result) : IProbeService
{
    public Task<IReadOnlyList<ProbeResult>> CheckAsync(AppConfig config, CancellationToken token) => result;
}
sealed class FakeEngine : IEngine
{
    public bool IsRunning { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public bool FailStart { get; init; }
    public void Start(AppConfig config) { Starts++; IsRunning = true; if (FailStart) throw new IOException("Startup failed"); }
    public void Stop() { Stops++; IsRunning = false; }
    public void Dispose() => Stop();
}

sealed class FakeInternetProxy : IInternetProxySettings
{
    public InternetProxyState State { get; set; } = new(null, 0);
    public InternetProxyState Read() => State;
    public void Write(InternetProxyState state) => State = state;
}
