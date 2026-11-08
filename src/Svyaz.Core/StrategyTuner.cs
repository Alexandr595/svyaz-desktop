using System.Net;

namespace Svyaz.Core;

public static class ServiceStrategies
{
    public static readonly string[] Services = ["ChatGPT", "Telegram", "Discord", "YouTube"];
    public static readonly string[] Candidates = ["z-fake-md5", "z-split", "z-disorder", "z-fake-seq", "z-faked-seq", "z-hostfake"];
    public static bool IsProfile(string id) => id == "direct" || ZapretEngine.Profiles.Any(p => p.Id == id && !p.TelegramSyn);
    public static string[] Actions(string id) => id == "direct" ? ["pass"] : ZapretEngine.Profiles.Single(p => p.Id == id && !p.TelegramSyn).Actions;
    public static string[] Domains(AppConfig config, string service) => config.Targets.Where(t =>
        ServiceCatalog.Presets.Single(p => p.Name == service).Domains.Any(d => t.Equals(d, StringComparison.OrdinalIgnoreCase) || t.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))).ToArray();
    public static AppConfig Direct(AppConfig config) => config with
    {
        TelegramProxy = "", TelegramProxyBackups = [], ChatGptProxy = "", ChatGptProxyBackups = [], DiscordVoice = false, DesktopProxy = false
    };
    public static string[] Eligible(AppConfig config) => Services.Where(s => Domains(config, s).Length > 0 &&
        !(s == "Telegram" && config.TelegramProxy.Length > 0) && !(s == "ChatGPT" && config.ChatGptProxy.Length > 0)).ToArray();
    public static string Title(string id) => id == "direct" ? "Без обработки" : ZapretEngine.Profiles.FirstOrDefault(p => p.Id == id)?.Title ?? id;
}

public sealed record ServiceAssessment(string Service, bool Healthy, string Detail);
public sealed record StrategySnapshot(IReadOnlyList<ProbeResult> Http, IReadOnlyList<ServiceAssessment> Services)
{
    public bool ControlsHealthy => Http.Any(r => r.IsCanary) && Http.Where(r => r.IsCanary).All(r => r.Healthy);
    public bool Preserves(StrategySnapshot before) => ControlsHealthy &&
        before.Http.Where(r => r.IsCanary || r.HttpsResponded).All(b => Http.Any(a => a.Url == b.Url && a.IsCanary == b.IsCanary && (b.IsCanary ? a.Healthy : a.HttpsResponded)));
    public bool Works(string service) => Services.Any(s => s.Service == service && s.Healthy);
    public static StrategySnapshot Stable(StrategySnapshot a, StrategySnapshot b) => new(
        a.Http.Select(x => b.Http.FirstOrDefault(y => y.Url == x.Url && y.IsCanary == x.IsCanary) is { } y
            ? y with { Healthy = x.Healthy && y.Healthy, StatusCode = x.HttpsResponded && y.HttpsResponded ? y.StatusCode : null }
            : x with { Healthy = false, StatusCode = null }).ToArray(),
        a.Services.Select(x => new ServiceAssessment(x.Service, x.Healthy && b.Works(x.Service),
            b.Services.FirstOrDefault(y => y.Service == x.Service)?.Detail ?? "Нет повторного результата")).ToArray());
}

public interface IStrategyProbe { Task<StrategySnapshot> CheckAsync(AppConfig config, CancellationToken token); }

public sealed class DirectStrategyProbe : IStrategyProbe
{
    public async Task<StrategySnapshot> CheckAsync(AppConfig config, CancellationToken token)
    {
        config = ServiceStrategies.Direct(config);
        var http = await new HttpsProbeService().CheckAsync(config, token).ConfigureAwait(false);
        var services = new List<ServiceAssessment>();
        foreach (var service in ServiceStrategies.Services.Where(s => ServiceStrategies.Domains(config, s).Length > 0))
        {
            var url = ServiceCatalog.Presets.Single(p => p.Name == service).ProbeUrl;
            var page = http.FirstOrDefault(r => r.Url == url);
            var healthy = page?.Healthy == true;
            var detail = page?.Detail ?? "Нет результата HTTPS";
            if (healthy && service == "Discord")
            {
                using var relay = new DirectProxy(config.InterfaceId); relay.Start();
                var gateway = await DiscordProbe.CheckAsync(new Uri($"http://127.0.0.1:{relay.Port}"), token).ConfigureAwait(false);
                healthy = gateway.Healthy; detail = gateway.Detail;
            }
            else if (healthy && service == "Telegram")
            {
                var native = await TelegramProbe.CheckAsync(config, token, connector: (_, _, ct) => TelegramWebSocket.ConnectAsync(2, config, ct)).ConfigureAwait(false);
                healthy = native.Healthy; detail = native.Detail;
            }
            else if (healthy && service == "YouTube")
            {
                var body = await HttpsProbeService.ProbeAsync(url, false, config.TimeoutSeconds, token, config.InterfaceId,
                    readBodyBytes: 32768).ConfigureAwait(false);
                healthy = body.StatusCode == 200 && body.BodyBytes > 0; detail = body.Detail + "; видео не проверено";
            }
            services.Add(new(service, healthy, detail));
        }
        return new(http, services);
    }
}

public sealed record StrategyTrial(string Profile, StrategySnapshot Snapshot);
public sealed record StrategyTuneResult(bool Verified, bool Changed, AppConfig Config, string Detail, IReadOnlyList<StrategyTrial> Trials);

// Only explicit, bounded trials. Every engine start has a matching stop. The
// selected combination must preserve controls and every earlier HTTP response.
public sealed class StrategyTuner(IStrategyProbe probes, IEngine engine)
{
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(1);
    public string[] Candidates { get; init; } = ServiceStrategies.Candidates;
    public event Action<string>? Progress;
    public async Task<StrategyTuneResult> RunAsync(AppConfig original, CancellationToken token)
    {
        original = original.Validate();
        if (!ServiceStrategies.IsProfile(original.Profile) || original.Profile == "direct") throw new ArgumentException("Выберите обычный профиль zapret2 для подбора.");
        if (original.InterfaceId.Length == 0) throw new ArgumentException("Для прямого подбора выберите физический сетевой интерфейс.");
        if (engine.IsRunning) throw new InvalidOperationException("Остановите текущую обработку перед подбором.");
        var trials = new List<StrategyTrial>();
        var eligible = ServiceStrategies.Eligible(original);
        if (eligible.Length == 0) return new(false, false, original, "Нет сервисов с прямым маршрутом для подбора.", trials);
        var direct = original with { DiscordVoice = false, DesktopProxy = false };
        var baseline = await probes.CheckAsync(ServiceStrategies.Direct(direct), token).ConfigureAwait(false);
        if (!baseline.ControlsHealthy) return new(false, false, original, "Контрольные адреса недоступны. Подбор не запускался.", trials);
        var selected = new Dictionary<string, string>(original.ServiceProfiles);
        try
        {
            var current = await TryAsync(direct, "Текущие настройки").ConfigureAwait(false);
            var winners = new Dictionary<string, string>();
            foreach (var service in eligible)
            {
                if (current.Preserves(baseline) && current.Works(service)) winners[service] = original.ServiceProfiles.GetValueOrDefault(service, original.Profile);
                else if (baseline.Works(service)) winners[service] = "direct";
            }
            foreach (var profile in Candidates.Distinct())
            {
                if (winners.Count == eligible.Length) break;
                token.ThrowIfCancellationRequested();
                var candidate = await TryAsync(direct with { Profile = profile, ServiceProfiles = new() }, ServiceStrategies.Title(profile)).ConfigureAwait(false);
                if (!candidate.ControlsHealthy) return new(false, false, original, "Сбой контрольного адреса. Сохранены прежние настройки.", trials);
                if (!candidate.Preserves(baseline)) continue;
                foreach (var service in eligible.Where(s => !winners.ContainsKey(s) && candidate.Works(s))) winners[service] = profile;
            }
            if (winners.Count == 0) return new(false, false, original, "Рабочий прямой транспорт не найден. Сохранены прежние настройки.", trials);
            foreach (var pair in winners) selected[pair.Key] = pair.Value;
            var resultConfig = original with { ServiceProfiles = selected };
            var final = await TryAsync(resultConfig with { DiscordVoice = false, DesktopProxy = false }, "Повторная проверка сочетания профилей").ConfigureAwait(false);
            if (!final.Preserves(baseline) || winners.Keys.Any(s => !final.Works(s)) ||
                current.Services.Any(s => s.Healthy && !final.Works(s.Service)))
                return new(false, false, original, "Сочетание не прошло проверку. Сохранены прежние настройки.", trials);
            var changed = selected.Count != original.ServiceProfiles.Count || selected.Any(p => original.ServiceProfiles.GetValueOrDefault(p.Key) != p.Value);
            var missing = eligible.Where(s => !final.Works(s)).ToArray();
            return new(true, changed, resultConfig,
                "Проверены прямые транспорты: " + string.Join(", ", eligible.Where(final.Works)) +
                (missing.Length > 0 ? ". Доступ не подтверждён: " + string.Join(", ", missing) : "") + ". Голос, видео и аккаунты не проверены.", trials);
        }
        finally { engine.Stop(); }

        async Task<StrategySnapshot> TryAsync(AppConfig config, string title)
        {
            Progress?.Invoke(title);
            try
            {
                token.ThrowIfCancellationRequested();
                engine.Start(config.Validate());
                await Task.Delay(StartupDelay, token).ConfigureAwait(false);
                if (!engine.IsRunning) throw new IOException("Движок завершился во время подбора.");
                var a = await probes.CheckAsync(ServiceStrategies.Direct(config), token).ConfigureAwait(false);
                var b = a.ControlsHealthy ? await probes.CheckAsync(ServiceStrategies.Direct(config), token).ConfigureAwait(false) : a;
                token.ThrowIfCancellationRequested();
                if (!engine.IsRunning) throw new IOException("Движок завершился во время проверки.");
                var stable = StrategySnapshot.Stable(a, b);
                trials.Add(new(config.Profile, stable));
                return stable;
            }
            finally { engine.Stop(); if (engine.IsRunning) throw new IOException("Остановка движка не подтверждена."); }
        }
    }
}
