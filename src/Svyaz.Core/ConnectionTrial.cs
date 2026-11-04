namespace Svyaz.Core;

public sealed record TrialResult(string Profile, string Outcome, string? Error,
    IReadOnlyList<ProbeResult> Before, IReadOnlyList<ProbeResult> After, bool EngineStopped);

// Bounded comparison of actual probes. Engine exit always runs, including when a
// probe, cancellation, or driver startup fails. "Improved" is only an HTTP result.
public sealed class ConnectionTrial(IProbeService probes, IEngine engine)
{
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<TrialResult> RunAsync(AppConfig config, CancellationToken token)
    {
        IReadOnlyList<ProbeResult> before = [], after = [];
        string outcome = "Не проверено";
        string? error = null;
        var stopped = false;
        try
        {
            config = config.Validate();
            before = await probes.CheckAsync(config, token);
            _ = new HealthPolicy(before);
            token.ThrowIfCancellationRequested();
            engine.Start(config);
            await Task.Delay(StartupDelay, token);
            if (!engine.IsRunning) throw new InvalidOperationException("Движок завершился до проверки доступа.");
            after = await probes.CheckAsync(config, token);
            token.ThrowIfCancellationRequested();
            if (!engine.IsRunning) throw new InvalidOperationException("Движок завершился во время проверки доступа.");
            var regression = before.Where(r => r.IsCanary).Any(b => !after.Any(a => a.IsCanary && a.Url == b.Url && a.Healthy));
            var lostResponse = before.Where(r => !r.IsCanary && r.HttpsResponded && !r.ViaExternalProxy)
                .Any(b => !after.Any(a => !a.IsCanary && a.Url == b.Url && a.HttpsResponded));
            outcome = regression ? "Сбой контрольного адреса" :
                lostResponse ? "Потерян ранее доступный HTTPS-ответ" :
                after.Any(a => !a.IsCanary && a.Healthy && before.Any(b => !b.IsCanary && b.Url == a.Url && !b.Healthy))
                ? "Есть улучшение HTTP" : "Улучшение не подтверждено";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { outcome = "Проверка отменена"; }
        catch (Exception ex) { outcome = "Ошибка проверки"; error = ex.Message; }
        finally
        {
            try { engine.Stop(); stopped = !engine.IsRunning; }
            catch (Exception ex) { error = (error == null ? "" : error + "; ") + "Остановка: " + ex.Message; }
        }
        return new(config.Profile, outcome, error, before, after, stopped);
    }
}
