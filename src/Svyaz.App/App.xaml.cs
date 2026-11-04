using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Diagnostics;
using Svyaz.Core;

namespace Svyaz.App;

public partial class App : Application
{
    private InstanceLease? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--proxy-watch" && int.TryParse(e.Args[1], out var ownerPid))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var folder = Path.Combine(AppContext.BaseDirectory, "data");
            try
            {
                using var owner = Process.GetProcessById(ownerPid);
                var exited = owner.WaitForExitAsync();
                while (!exited.IsCompleted && File.Exists(Path.Combine(folder, "desktop-proxy-backup.json")))
                    await Task.WhenAny(exited, Task.Delay(1000));
            }
            catch (ArgumentException) { }
            try { DesktopProxyLease.Restore(folder, new WindowsInternetProxySettings(), ownerPid); }
            catch (Exception ex) { File.AppendAllText(Path.Combine(folder, "proxy-recovery.log"), DateTimeOffset.Now + " " + ex.Message + Environment.NewLine); }
            Shutdown(0); return;
        }
        if (e.Args.Contains("--stop", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var folder = Path.Combine(AppContext.BaseDirectory, "data"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "stop.request"), DateTimeOffset.UtcNow.ToString("O"));
            Shutdown(0); return;
        }
        var instanceDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        instance = InstanceLease.TryAcquire(instanceDirectory);
        if (instance == null && e.Args.Contains("--handoff", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            for (var attempt = 0; attempt < 100 && instance == null; attempt++)
            {
                await Task.Delay(100);
                instance = InstanceLease.TryAcquire(instanceDirectory);
            }
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }
        if (instance == null)
        {
            File.WriteAllText(Path.Combine(instanceDirectory, "show.request"), DateTimeOffset.UtcNow.ToString("O"));
            Shutdown(0); return;
        }
        DesktopProxyLease.Restore(instanceDirectory, new WindowsInternetProxySettings());
        if (!e.Args.Contains("--trial", StringComparer.Ordinal))
        {
            base.OnStartup(e);
            var window = new MainWindow();
            if (e.Args.Contains("--background", StringComparer.Ordinal)) window.ShowActivated = false;
            window.Show();
            return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        var directory = Path.Combine(AppContext.BaseDirectory, "data");
        var steps = new List<TrialResult>();
        var messages = new List<string>();
        string? error = null;
        try
        {
            Directory.CreateDirectory(directory);
            var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            if (!admin) throw new InvalidOperationException("Проверка движка не выполнялась: нужны права администратора.");
            var config = AppConfig.Load(Path.Combine(directory, "settings.json"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(e.Args.Contains("--scan") ? 240 : 90));
            using var engine = new AdaptiveEngine(AppContext.BaseDirectory, directory);
            engine.Output += line => { lock (messages) messages.Add(line); };
            var profiles = e.Args.Contains("--scan") ? ZapretEngine.Profiles.Select(p => p.Id).ToArray() : new[] { config.Profile };
            foreach (var profile in profiles)
            {
                var result = await new ConnectionTrial(new HttpsProbeService(), engine)
                    .RunAsync(config with { Profile = profile }, timeout.Token);
                steps.Add(result);
                File.WriteAllText(Path.Combine(directory, "trial-progress.json"), JsonSerializer.Serialize(steps, new JsonSerializerOptions { WriteIndented = true }));
                if (!result.EngineStopped || result.Outcome is "Сбой контрольного адреса" or "Ошибка проверки" or "Проверка отменена") break;
            }
        }
        catch (Exception ex) { error = ex.Message; }
        try
        {
            string[] output;
            lock (messages) output = messages.ToArray();
            File.WriteAllText(Path.Combine(directory, "trial-last.json"), JsonSerializer.Serialize(new
            {
                checkedAt = DateTimeOffset.Now, error, steps, engineOutput = output,
                note = "Проверка HTTP до/после. Не подтверждает работу приложений целиком и не определяет провайдера."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось записать результат проверки"); }
        Shutdown(error != null || steps.Any(s => s.Error != null || !s.EngineStopped) ? 1 : 0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                var directory = Path.Combine(AppContext.BaseDirectory, "data");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "crash.log"), DateTimeOffset.Now + " " + e.Exception + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            MessageBox.Show(e.Exception.Message, "Связь · ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            e.Handled = true;
        };
    }
}
