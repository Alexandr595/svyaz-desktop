using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Svyaz.Core;

namespace Svyaz.App;

public partial class MainWindow
{
    private bool tuning;
    private NetworkProfileStore NetworkProfiles => new(Path.Combine(dataDirectory, "network-profiles.json"));

    private AppConfig ApplyNetworkProfile(AppConfig current)
    {
        if (!current.RememberNetworkProfiles || current.InterfaceId.Length == 0) return current;
        try
        {
            var saved = NetworkProfiles.Find(current, NetworkProfileStore.Signature(current.InterfaceId));
            if (saved != null)
            {
                AddLog("Загружены проверенные профили для этого интерфейса и названия сети.");
                current = saved;
            }
            else if (current.ServiceProfiles.Count > 0)
            {
                current = current with { ServiceProfiles = new() };
                AddLog("Для этой сети сохранённого результата нет. Используется общий профиль.");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
        {
            current = current with { ServiceProfiles = new() };
            AddLog("Сетевые профили: " + ex.Message + " Используется общий профиль.");
        }
        RenderServiceProfiles(current);
        return current;
    }

    private void RenderServiceProfiles(AppConfig current) => StrategyStatus.Text = current.ServiceProfiles.Count == 0
        ? "Для сервисов используется общий профиль. Подбор проверяет прямой HTTPS и доступные транспорты."
        : string.Join(Environment.NewLine, current.ServiceProfiles.Select(p => p.Key + " — " + ServiceStrategies.Title(p.Value)));

    private void ResetProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (operation != null) return;
        try
        {
            var current = ReadConfig();
            if (current.InterfaceId.Length != 0) NetworkProfiles.Remove(current, NetworkProfileStore.Signature(current.InterfaceId));
            config = current with { ServiceProfiles = new() }; config.Save(ConfigPath); RenderServiceProfiles(config);
            AddLog("Отдельные профили этой сети сброшены. Будет применён общий профиль.");
        }
        catch (Exception ex) { StrategyStatus.Text = ex.Message; }
    }

    private async Task<AppConfig?> TuneProfilesAsync(AppConfig current, CancellationToken token)
    {
        tuning = true;
        var tuner = new StrategyTuner(new DirectStrategyProbe(), engine);
        tuner.Progress += text => OnUi(() => { StrategyStatus.Text = text; AddLog("[подбор] " + text); });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var result = await tuner.RunAsync(current, deadline.Token);
            File.WriteAllText(Path.Combine(dataDirectory, "strategy-last.json"), JsonSerializer.Serialize(new
            {
                checkedAt = DateTimeOffset.Now, result.Verified, result.Changed, result.Detail,
                profiles = result.Config.ServiceProfiles, result.Trials
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (closing || token.IsCancellationRequested) return null;
            StrategyStatus.Text = result.Detail;
            AddLog("[подбор] " + result.Detail);
            if (!result.Verified) return null;
            var selected = result.Config;
            if (selected.RememberNetworkProfiles)
            {
                try { NetworkProfiles.Save(selected, NetworkProfileStore.Signature(selected.InterfaceId)); }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
                { AddLog("Профиль действует в этом сеансе; хранилище сети: " + ex.Message); }
            }
            config = selected;
            config.Save(ConfigPath);
            return selected;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { StrategyStatus.Text = "Подбор завершён по времени. Сохранены прежние настройки."; return null; }
        finally { tuning = false; }
    }

    private async void TuneProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (operation != null || tuning) return;
        try
        {
            var current = ReadConfig();
            if (!IsAdmin)
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--tune --handoff")
                    { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory });
                Close(); return;
            }
            StopProxy();
            operation = new CancellationTokenSource(); stopRequested = false; SetBusy(true);
            ShowState(SessionState.Checking, "Подбираем прямые профили. Это может занять до пяти минут…");
            runningTask = TuneProfilesAsync(current, operation.Token);
            await runningTask;
        }
        catch (OperationCanceledException) { if (!closing) StrategyStatus.Text = "Подбор отменён. Прежние настройки сохранены."; }
        catch (Exception ex) { if (!closing) { StrategyStatus.Text = "Подбор: " + ex.Message; AddLog(StrategyStatus.Text); } }
        finally
        {
            EndOperation();
            if (!closing) ShowState(SessionState.Idle, "Подбор завершён. Нажмите «Подключить», чтобы применить сохранённые профили.");
        }
    }
}
