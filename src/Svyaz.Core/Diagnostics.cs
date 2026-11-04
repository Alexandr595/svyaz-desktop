using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Svyaz.Core;

public sealed record ProbeResult(string Url, bool IsCanary, bool Healthy, int? StatusCode,
    long ElapsedMs, string Detail, DateTimeOffset CheckedAt)
{
    public bool HttpsResponded => StatusCode.HasValue;
    public bool AccessNeedsBrowserCheck => StatusCode is 401 or 403;
    public bool ViaExternalProxy { get; init; }
}

public interface IProbeService
{
    Task<IReadOnlyList<ProbeResult>> CheckAsync(AppConfig config, CancellationToken token);
}

public sealed class HttpsProbeService : IProbeService
{
    public Uri? LocalProxy { get; set; }
    public async Task<IReadOnlyList<ProbeResult>> CheckAsync(AppConfig config, CancellationToken token)
    {
        using var limiter = new SemaphoreSlim(6);
        var localProxy = LocalProxy;
        var endpoints = config.Canaries.Select(url => (url, canary: true))
            .Concat(ServiceCatalog.ProbeUrls(config.Targets).Select(url => (url, canary: false)));
        return await Task.WhenAll(endpoints.Select(async endpoint =>
        {
            await limiter.WaitAsync(token);
            try { return await ProbeAsync(endpoint.Item1, endpoint.canary, config.TimeoutSeconds, token, config.InterfaceId, config.TelegramProxy, config.TelegramProxyBackups, config.ChatGptProxy, config.ChatGptProxyBackups, localProxy); }
            finally { limiter.Release(); }
        }));
    }

    public static async Task<ProbeResult> ProbeAsync(string url, bool isCanary, int timeoutSeconds,
        CancellationToken token, string interfaceId = "", string telegramProxy = "", string[]? telegramProxyBackups = null,
        string chatGptProxy = "", string[]? chatGptProxyBackups = null, Uri? localProxy = null)
    {
        var watch = Stopwatch.StartNew();
        // A fresh direct HTTP/1.1 connection checks a new TLS handshake on every pass.
        // System proxies and redirects are deliberately excluded from this transport measurement.
        using var handler = new SocketsHttpHandler
        {
            UseProxy = localProxy != null, Proxy = localProxy == null ? null : new WebProxy(localProxy),
            AllowAutoRedirect = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
        if (localProxy == null && (interfaceId.Length != 0 || telegramProxy.Length != 0 || chatGptProxy.Length != 0))
            handler.ConnectCallback = (context, cancellation) =>
                TelegramRoute.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port,
                    new AppConfig { InterfaceId = interfaceId, TelegramProxy = telegramProxy, TelegramProxyBackups = telegramProxyBackups ?? [],
                        ChatGptProxy = chatGptProxy, ChatGptProxyBackups = chatGptProxyBackups ?? [] }, cancellation);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var targetHost = new Uri(url).IdnHost;
        var viaExternal = telegramProxy.Length != 0 && TelegramRoute.IsTelegram(targetHost) ||
                          chatGptProxy.Length != 0 && TelegramRoute.IsChatGpt(targetHost);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.UserAgent.ParseAdd("Svyaz/0.2.1");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var code = (int)response.StatusCode;
            return new(url, isCanary, code is >= 200 and < 400, code, watch.ElapsedMilliseconds,
                code is >= 300 and < 400 ? $"HTTP {code} · перенаправление, переход не проверен" : $"HTTP {code}", DateTimeOffset.Now)
                { ViaExternalProxy = viaExternal };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Failure("Тайм-аут DNS / TCP / TLS / HTTP"); }
        catch (HttpRequestException ex)
        {
            var detail = ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "Ошибка DNS",
                HttpRequestError.SecureConnectionError => "Ошибка TLS / сертификата",
                HttpRequestError.ConnectionError => "Ошибка TCP-соединения",
                _ => "Ошибка HTTP-соединения"
            };
            return Failure(detail);
        }
        catch (Exception ex) when (ex is SocketException or AuthenticationException or IOException)
        { return Failure("Ошибка сетевого соединения"); }

        ProbeResult Failure(string detail) => new(url, isCanary, false, null,
            watch.ElapsedMilliseconds, detail, DateTimeOffset.Now) { ViaExternalProxy = viaExternal };
    }
}

public sealed class HealthPolicy
{
    private readonly Dictionary<string, (bool IsCanary, int Failures)> failures = new(StringComparer.OrdinalIgnoreCase);
    public HealthPolicy(IReadOnlyList<ProbeResult> baseline)
    {
        var canaries = baseline.Where(r => r.IsCanary).ToArray();
        if (canaries.Length == 0 || canaries.Any(r => !r.Healthy))
            throw new InvalidOperationException("Контрольные сервисы должны отвечать HTTP 2xx/3xx до запуска. Исправьте список или сеть.");
        foreach (var result in baseline.Where(r => r.IsCanary || r.HttpsResponded && !r.ViaExternalProxy))
            failures.Add(result.Url, (result.IsCanary, 0));
    }

    public string? Observe(IReadOnlyList<ProbeResult> results)
    {
        foreach (var url in failures.Keys.ToArray())
        {
            var watched = failures[url];
            var result = results.FirstOrDefault(r => r.IsCanary == watched.IsCanary && r.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            var answered = watched.IsCanary ? result?.Healthy == true : result?.HttpsResponded == true;
            var count = answered ? 0 : watched.Failures + 1;
            failures[url] = (watched.IsCanary, count);
            if (count >= 2) return new Uri(url).Host;
        }
        return null;
    }
}
