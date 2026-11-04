using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Svyaz.Core;

public sealed record DpiProfile(string Id, string Title, string[] Actions, bool TelegramSyn = false);

public sealed class ZapretEngine(string directory, string dataDirectory) : IEngine
{
    public static IReadOnlyList<DpiProfile> Profiles { get; } =
    [
        new("z-split", "zapret2 · разбиение SNI", ["multisplit:pos=1,midsld"]),
        new("z-disorder", "zapret2 · обратный порядок", ["multidisorder:pos=1,midsld"]),
        new("z-overlap", "zapret2 · перекрытие", ["multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google"]),
        new("z-fake-md5", "zapret2 · fake MD5", ["fake:blob=fake_default_tls:tcp_md5:repeats=6:tls_mod=rnd,rndsni,dupsid", "multisplit:pos=1,midsld"]),
        new("z-fake-seq", "zapret2 · fake sequence", ["fake:blob=fake_default_tls:tcp_seq=-10000:tcp_ack=-66000:repeats=6:tls_mod=rnd,rndsni,dupsid", "multisplit:pos=1,midsld"]),
        new("z-fake-sum", "zapret2 · fake checksum", ["fake:blob=fake_default_tls:badsum:repeats=6:tls_mod=rnd,rndsni,dupsid", "multisplit:pos=1,midsld"]),
        new("z-faked-md5", "zapret2 · смешанное разбиение", ["fakedsplit:pos=midsld:tcp_md5"]),
        new("z-faked-seq", "zapret2 · смешанный порядок", ["fakeddisorder:pos=midsld:tcp_seq=-10000:tcp_ack=-66000"]),
        new("z-hostfake", "zapret2 · host fake", ["hostfakesplit:host=www.google.com:midhost=midsld:tcp_md5"])
        ,new("z-telegram", "zapret2 · Telegram SYN + TLS", ["fake:blob=fake_default_tls:tcp_md5:repeats=6:tls_mod=rnd,rndsni,dupsid", "multisplit:pos=1,midsld"], true)
    ];
    public static readonly IReadOnlyDictionary<string, string> Hashes = new Dictionary<string, string>
    {
        ["winws2.exe"] = "BA4CECCDEA3576DA0A0991A50D7893EA911DE197281338132DBEC02A5B2FCF58",
        ["cygwin1.dll"] = "103104A52E5293CE418944725DF19E2BF81AD9269B9A120D71D39028E821499B",
        ["WinDivert.dll"] = "16ABD6A029E65557C6A309BEA7B13BF81FFF4E193567582E1CDDBF6719F323E0",
        ["WinDivert64.sys"] = "8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2",
        ["zapret-lib.lua"] = "6BF528FEF1F0FC3C65E189216FC8A06BB873F6E25D1C29D2BFA4695A09B73440",
        ["zapret-antidpi.lua"] = "6D1FA9168E5DE1682FEF91FD174F68B58C50F6739EF00F42CA7DB2B940B6246D",
        ["tls_clienthello_www_google_com.bin"] = "936C2BEE4CFB80AA3C426B2DCBCC834B3FBCD1ADB17172959DC569C73A14275C",
        ["stun.bin"] = "9CD5469309780CA56C0BD97266524A48C7EE529D02C3179CFECB20B260A59641"
    };
    private Process? process;
    private WindowsJob? job;
    public event Action<string>? Output;
    public bool IsRunning => process is { HasExited: false };
    public int? ExitCode => process is { HasExited: true } ? process.ExitCode : null;

    public static void VerifyBundle(string path)
    {
        foreach (var (name, hash) in Hashes)
        {
            var file = Path.Combine(path, name);
            if (!File.Exists(file) || !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Файл zapret2 отсутствует или изменён: {name}");
        }
    }

    public static string[] Arguments(AppConfig config, string hostList, string excludeList)
    {
        var profile = Profiles.Single(p => p.Id == config.Profile);
        const string tcpPorts = "443";
        var args = new List<string>
        {
            "--wf-tcp-out=" + tcpPorts, "--lua-init=@zapret-lib.lua", "--lua-init=@zapret-antidpi.lua",
            "--blob=tls_google:@tls_clienthello_www_google_com.bin"
        };
        if (config.InterfaceId.Length != 0)
        {
            var choice = DirectNetwork.Choices().SingleOrDefault(n => n.Id == config.InterfaceId)
                ?? throw new IOException("Выбранный интерфейс недоступен.");
            args.Insert(0, "--wf-iface=" + choice.Index);
        }
        if (config.DiscordVoice && config.Targets.Contains("discord.com", StringComparer.OrdinalIgnoreCase))
        {
            // Discord's binary IP discovery identifies this flow. Generic STUN,
            // DNS, QUIC and unknown UDP from other applications stay untouched.
            const string voicePorts = "19294-19344,50000-65535";
            args.AddRange(["--wf-udp-out=" + voicePorts, "--blob=svyaz_stun:@stun.bin",
                "--filter-udp=" + voicePorts, "--filter-l7=discord", "--payload=discord_ip_discovery",
                "--out-range=-d8", "--lua-desync=fake:blob=svyaz_stun:repeats=6:ip_ttl=4", "--new"]);
        }
        if (profile.TelegramSyn && config.Targets.Contains("telegram.org", StringComparer.OrdinalIgnoreCase))
        {
            var ips = Dns.GetHostAddresses("web.telegram.org").Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString());
            args.AddRange(["--filter-tcp=443", "--ipset-ip=" + string.Join(',', ips), "--payload=empty",
                "--lua-desync=syndata:blob=tls_google", "--new"]);
        }
        args.AddRange(["--filter-tcp=" + tcpPorts, "--filter-l7=tls", "--hostlist=" + hostList,
            "--hostlist-exclude=" + excludeList, "--payload=tls_client_hello", "--out-range=-d4"]);
        var proxyIps = new[] { config.TelegramProxy, config.ChatGptProxy }.Concat(config.TelegramProxyBackups).Concat(config.ChatGptProxyBackups)
            .Where(p => p.Length != 0).Select(p => TelegramRoute.ParseProxy(p)!.Host)
            .Where(host => IPAddress.TryParse(host, out _)).Distinct().ToArray();
        if (proxyIps.Length != 0) args.Add("--ipset-exclude-ip=" + string.Join(',', proxyIps));
        args.AddRange(profile.Actions.Select(a => "--lua-desync=" + a));
        return args.ToArray();
    }

    public void Start(AppConfig config)
    {
        config = config.Validate();
        if (IsRunning) throw new InvalidOperationException("Движок уже работает.");
        VerifyBundle(directory);
        foreach (var name in new[] { "goodbyedpi", "winws", "winws2" })
        {
            var existing = Process.GetProcessesByName(name);
            try { if (existing.Length != 0) throw new InvalidOperationException($"Уже работает {name}. Остановите его перед проверкой."); }
            finally { foreach (var item in existing) item.Dispose(); }
        }
        Directory.CreateDirectory(dataDirectory);
        var hosts = Path.GetFullPath(Path.Combine(dataDirectory, "active-hosts.txt"));
        var exclude = Path.GetFullPath(Path.Combine(dataDirectory, "exclude-hosts.txt"));
        File.WriteAllLines(hosts, config.Targets, new UTF8Encoding(false));
        File.WriteAllLines(exclude, config.Canaries.Select(c => new Uri(c).IdnHost), new UTF8Encoding(false));
        var start = new ProcessStartInfo(Path.Combine(directory, "winws2.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in Arguments(config, hosts, exclude)) start.ArgumentList.Add(arg);
        job = new WindowsJob();
        try
        {
            process = Process.Start(start) ?? throw new IOException("Не удалось запустить winws2.");
            job.Assign(process);
            process.OutputDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
        catch { Stop(); throw; }
    }
    public void Stop()
    {
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(3000)) throw new IOException("Не удалось подтвердить остановку winws2.");
        }
        job?.Dispose(); job = null;
        process?.Dispose(); process = null;
    }
    public void Dispose() { try { Stop(); } finally { job?.Dispose(); } }
}

public sealed class AdaptiveEngine : IEngine
{
    private readonly GoodbyeEngine goodbye;
    private readonly ZapretEngine zapret;
    private IEngine? active;
    public event Action<string>? Output;
    public AdaptiveEngine(string baseDirectory, string dataDirectory)
    {
        goodbye = new GoodbyeEngine(Path.Combine(baseDirectory, "engine"), dataDirectory);
        zapret = new ZapretEngine(Path.Combine(baseDirectory, "engine", "zapret2"), dataDirectory);
        goodbye.Output += line => Output?.Invoke(line);
        zapret.Output += line => Output?.Invoke(line);
    }
    public bool IsRunning => active?.IsRunning == true;
    public int? ExitCode => active?.ExitCode;
    public void Start(AppConfig config)
    {
        if (IsRunning) throw new InvalidOperationException("Движок уже работает.");
        active = config.Profile.StartsWith("z-", StringComparison.Ordinal) ? zapret : goodbye;
        active.Start(config);
    }
    public void Stop() => active?.Stop();
    public void Dispose() { try { goodbye.Dispose(); } finally { zapret.Dispose(); } }
}
