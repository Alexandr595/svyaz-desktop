namespace Svyaz.Core;

public interface IEngine : IDisposable
{
    bool IsRunning { get; }
    int? ExitCode => null;
    void Start(AppConfig config);
    void Stop();
}

public enum SessionState { Idle, Checking, Running, RolledBack, Faulted }

public sealed class SessionController(IProbeService probes, IEngine engine)
{
    private int active;
    public event Action<SessionState, string>? StateChanged;
    public event Action<IReadOnlyList<ProbeResult>>? ResultsChanged;
    public event Action<string>? Log;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan? IntervalOverride { get; init; }

    public async Task RunAsync(AppConfig config, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("Сеанс уже запущен.");
        var terminal = SessionState.Idle;
        var message = "Обработка выключена";
        try
        {
            config = config.Validate();
            StateChanged?.Invoke(SessionState.Checking, "Проверяем исходное соединение…");
            var baseline = await probes.CheckAsync(config, token);
            token.ThrowIfCancellationRequested();
            ResultsChanged?.Invoke(baseline);
            var policy = new HealthPolicy(baseline);
            Log?.Invoke($"Запуск профиля {config.Profile}; контрольные адреса: {baseline.Count(r => r.IsCanary && r.Healthy)}/{baseline.Count(r => r.IsCanary)}.");
            engine.Start(config);
            Log?.Invoke("Движок запущен. Выборочная обработка TLS на TCP 443; контрольные сервисы исключены из списка.");
            await Task.Delay(InitialDelay, token);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!engine.IsRunning) throw new InvalidOperationException($"Движок обработки завершился, код {engine.ExitCode?.ToString() ?? "неизвестен"}. Подробности — в журнале.");
                var results = await probes.CheckAsync(config, token);
                token.ThrowIfCancellationRequested();
                if (!engine.IsRunning) throw new InvalidOperationException("Движок обработки завершился во время проверки.");
                ResultsChanged?.Invoke(results);
                var regression = policy.Observe(results);
                if (regression != null)
                {
                    terminal = SessionState.RolledBack;
                    message = $"Автоостановка: потерян ранее доступный ответ у {regression} два раза подряд";
                    Log?.Invoke(message + ". Причина сбоя может быть внешней; обработка остановлена для проверки.");
                    break;
                }
                var targets = results.Where(r => !r.IsCanary).ToArray();
                var controlsOk = results.Where(r => r.IsCanary).All(r => r.Healthy);
                var improved = targets.Count(r => r.Healthy && baseline.Any(b => !b.IsCanary && b.Url == r.Url && !b.Healthy));
                StateChanged?.Invoke(SessionState.Running, controlsOk
                    ? improved > 0
                        ? $"Движок работает · HTTP улучшился у {improved} адресов; функции сервисов проверьте вручную"
                        : "Движок запущен, улучшение доступа не подтверждено"
                    : "Сбой контрольного сервиса · повторная проверка через интервал");
                await Task.Delay(IntervalOverride ?? TimeSpan.FromSeconds(config.IntervalSeconds), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Log?.Invoke("Остановлено пользователем."); }
        catch (Exception ex)
        {
            terminal = SessionState.Faulted;
            message = ex.Message;
            Log?.Invoke("Ошибка: " + ex.Message);
        }
        finally
        {
            try { engine.Stop(); }
            catch (Exception ex)
            {
                terminal = SessionState.Faulted;
                message = "Не удалось подтвердить остановку движка: " + ex.Message;
                Log?.Invoke(message);
            }
            Interlocked.Exchange(ref active, 0);
            StateChanged?.Invoke(terminal, message);
        }
    }
}
