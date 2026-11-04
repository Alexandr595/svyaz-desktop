using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Svyaz.Core;
using System.Windows.Threading;
using System.Windows.Media.Animation;

namespace Svyaz.App;

public partial class MainWindow : Window
{
    private readonly string dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
    private readonly HttpsProbeService probes = new();
    private readonly AdaptiveEngine engine;
    private DirectProxy? proxy;
    private DesktopProxyLease? desktopProxy;
    private TelegramDesktopBridge? telegramBridge;
    private readonly Queue<double> latencyHistory = new();
    private DateTimeOffset? connectedAt;
    private int failedChecks;
    private bool nativeChecking;
    private bool chatGptSetupBusy;
    private bool nativeCheckLaunched;
    private readonly SessionController session;
    private readonly SessionJournal journal;
    private readonly List<string> log = [];
    private AppConfig config = new();
    private IReadOnlyList<ProbeResult> results = [];
    private CancellationTokenSource? operation;
    private Task? runningTask;
    private bool closing;
    private bool configError;
    private readonly DateTime startedAt = DateTime.UtcNow;
    private DateTime lastShowRequest = DateTime.UtcNow;
    private bool stopRequested;
    private bool openBrowserWhenReady;
    private SessionState lastState = SessionState.Idle;
    private string lastMessage = "Обработка выключена";
    private readonly DispatcherTimer controlTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string ConfigPath => Path.Combine(dataDirectory, "settings.json");
    private static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public MainWindow()
    {
        InitializeComponent();
        journal = new SessionJournal(dataDirectory);
        engine = new AdaptiveEngine(AppContext.BaseDirectory, dataDirectory);
        session = new SessionController(probes, engine);
        engine.Output += text => OnUi(() => AddLog("[движок] " + text));
        session.Log += text => OnUi(() => AddLog(text));
        session.StateChanged += (state, text) => OnUi(() => ShowState(state, text));
        session.ResultsChanged += list => OnUi(() => ShowResults(list));
        try { config = AppConfig.Load(ConfigPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            configError = true;
            AddLog("Настройки не загружены: " + ex.Message + " Исходный файл сохранён. Исправьте настройки и явно сохраните их.");
        }
        TargetsBox.Text = string.Join(Environment.NewLine, config.Targets);
        CanariesBox.Text = string.Join(Environment.NewLine, config.Canaries);
        ProfileBox.ItemsSource = ZapretEngine.Profiles.Concat(new[]
        {
            new DpiProfile("gentle", "GoodbyeDPI · прежний мягкий", []),
            new DpiProfile("sni", "GoodbyeDPI · прежний SNI", [])
        }).ToArray();
        ProfileBox.DisplayMemberPath = nameof(DpiProfile.Title);
        ProfileBox.SelectedValuePath = nameof(DpiProfile.Id);
        ProfileBox.SelectedValue = config.Profile;
        var choices = DirectNetwork.Choices();
        InterfaceBox.ItemsSource = new[] { new NetworkChoice("", "Маршрут Windows", 0, "") }.Concat(choices).ToArray();
        InterfaceBox.DisplayMemberPath = nameof(NetworkChoice.Name);
        InterfaceBox.SelectedValuePath = nameof(NetworkChoice.Id);
        InterfaceBox.SelectedValue = config.InterfaceId.Length == 0 ? choices.FirstOrDefault(n => n.Name == "Ethernet")?.Id ?? "" : config.InterfaceId;
        IntervalBox.Text = config.IntervalSeconds.ToString();
        NetworkBox.Text = config.NetworkName;
        TelegramProxyBox.Text = config.TelegramProxy;
        ChatGptProxyBox.Text = config.ChatGptProxy;
        OpenBrowserBox.IsChecked = config.OpenBrowserOnStart;
        DesktopProxyBox.IsChecked = config.DesktopProxy;
        DiscordVoiceBox.IsChecked = config.DiscordVoice;
        GlassBox.IsChecked = config.GlassAppearance;
        AdminLabel.Text = IsAdmin ? "Права администратора доступны" : "Для обработки понадобится запуск от администратора";
        try { ZapretEngine.VerifyBundle(Path.Combine(AppContext.BaseDirectory, "engine", "zapret2")); EngineLabel.Text = "zapret2 1.0.6 · SHA-256 проверен"; }
        catch (Exception ex) { EngineLabel.Text = "Движок недоступен · см. журнал"; AddLog(ex.Message); }
        if (configError) ShowError(new ArgumentException("Файл настроек повреждён. Проверьте значения и нажмите «Сохранить настройки»."));
        AddLog("Готово к работе. Добавлены стартовые списки ChatGPT, Telegram, Discord, YouTube. Контрольные адреса замените своими критичными сервисами.");
        AddLog("TLS/TCP 443; UDP Discord по умолчанию проходит без изменений. Telegram Desktop использует локальный MTProxy. Голос проверяется в самом Discord; HTTP 200 его не подтверждает.");
        NativeChatGptState.Text = config.ChatGptProxy.Length == 0 ? "Внешний SOCKS5 ChatGPT не настроен; сейчас используется прямой выход. Нативному клиенту также требуется разрешить системный маршрут." : "SOCKS5 для ChatGPT настроен. Разрешите системный маршрут кнопкой справа и полностью перезапустите ChatGPT после завершения текущей работы.";
        Loaded += (_, _) => { if (Environment.GetCommandLineArgs().Contains("--start")) Start_Click(this, new RoutedEventArgs()); };
        controlTimer.Tick += (_, _) =>
        {
            UpdateLiveMetrics();
            if (File.GetLastWriteTimeUtc(Path.Combine(dataDirectory, "stop.request")) > startedAt) Close();
            var showRequest = File.GetLastWriteTimeUtc(Path.Combine(dataDirectory, "show.request"));
            if (showRequest > lastShowRequest)
            {
                lastShowRequest = showRequest;
                Show(); WindowState = WindowState.Normal; Activate();
            }
        };
        Loaded += (_, _) => controlTimer.Start();
        Closed += (_, _) => controlTimer.Stop();
        InitializeUpdates();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => ApplyAppearance();
    private void ApplyAppearance()
    {
        var glass = WindowBackdrop.Apply(this, GlassBox.IsChecked == true);
        ShellFrame.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(glass ? "#6B0B1221" : "#FF0B1221"));
    }
    private void Glass_Click(object sender, RoutedEventArgs e)
    {
        ApplyAppearance();
        if (configError) return;
        try { config = config with { GlassAppearance = GlassBox.IsChecked == true }; config.Save(ConfigPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddLog("Не удалось сохранить оформление: " + ex.Message); }
    }
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (ShellFrame == null || MaximizeButton == null) return;
        ShellFrame.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(16);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private AppConfig ReadConfig(bool explicitSave = false)
    {
        if (configError && !explicitSave) throw new ArgumentException("Сначала явно сохраните исправленные настройки.");
        if (!int.TryParse(IntervalBox.Text, out var interval)) throw new ArgumentException("Интервал задаётся целым числом секунд.");
        var next = (config with
        {
            Targets = Lines(TargetsBox.Text), Canaries = Lines(CanariesBox.Text),
            Profile = (string?)ProfileBox.SelectedValue ?? "z-fake-md5", IntervalSeconds = interval,
            NetworkName = NetworkBox.Text.Trim(), InterfaceId = (string?)InterfaceBox.SelectedValue ?? "",
            TelegramProxy = TelegramProxyBox.Text.Trim(), ChatGptProxy = ChatGptProxyBox.Text.Trim(),
            OpenBrowserOnStart = OpenBrowserBox.IsChecked == true,
            DesktopProxy = DesktopProxyBox.IsChecked == true, DiscordVoice = DiscordVoiceBox.IsChecked == true,
            GlassAppearance = GlassBox.IsChecked == true
        }).Validate();
        next.Save(ConfigPath);
        config = next;
        configError = false;
        return next;
    }
    private static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (operation != null) return;
        try
        {
            var current = ReadConfig();
            operation = new CancellationTokenSource();
            SetBusy(true);
            ShowState(SessionState.Checking, "Проверяем прямое HTTPS-соединение…");
            runningTask = CheckOnce(current, operation.Token);
            await runningTask;
        }
        catch (Exception ex) { if (!closing) ShowError(ex); }
        finally { EndOperation(); }
    }

    private async Task CheckOnce(AppConfig current, CancellationToken token)
    {
        try
        {
            var list = await probes.CheckAsync(current, token);
            token.ThrowIfCancellationRequested();
            if (closing) return;
            ShowResults(list);
            ShowState(SessionState.Idle, "Проверка завершена · обработка выключена");
            AddLog($"Проверка: HTTP 2xx/3xx у {list.Count(r => r.Healthy)} из {list.Count} адресов.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { if (!closing) ShowState(SessionState.Idle, "Проверка остановлена"); }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (operation != null) return;
        try
        {
            var current = ReadConfig();
            if (current.Profile.StartsWith("z-", StringComparison.Ordinal)) ZapretEngine.VerifyBundle(Path.Combine(AppContext.BaseDirectory, "engine", "zapret2"));
            else GoodbyeEngine.VerifyBundle(Path.Combine(AppContext.BaseDirectory, "engine"));
            if (!IsAdmin)
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь приложения.");
                Process.Start(new ProcessStartInfo(executable, "--start --handoff") { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory });
                Close();
                return;
            }
            operation = new CancellationTokenSource();
            stopRequested = false;
            SetBusy(true);
            StopProxy();
            proxy = new DirectProxy(current.InterfaceId, (host, port, token) => TelegramRoute.ConnectAsync(host, port, current, token), current.LocalProxyPort);
            proxy.Error += text => OnUi(() => AddLog("[прокси] " + text));
            proxy.Start();
            telegramBridge = new TelegramDesktopBridge(current, dataDirectory);
            telegramBridge.Error += text => OnUi(() => AddLog(text));
            telegramBridge.Start();
            proxy.PacScript = DesktopPac.Create(current.Targets, proxy.Port);
            if (current.DesktopProxy)
            {
                desktopProxy = new DesktopProxyLease(dataDirectory, proxy.Port, new WindowsInternetProxySettings());
                try
                {
                    var watcher = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                    watcher.ArgumentList.Add("--proxy-watch"); watcher.ArgumentList.Add(Environment.ProcessId.ToString());
                    Process.Start(watcher)?.Dispose();
                }
                catch { desktopProxy.Dispose(); desktopProxy = null; throw; }
                AddLog("Системный PAC включён только для выбранных доменов. Предыдущие настройки сохранены и восстанавливаются при остановке, закрытии и аварии.");
            }
            connectedAt = DateTimeOffset.Now; failedChecks = 0;
            nativeCheckLaunched = false;
            openBrowserWhenReady = current.OpenBrowserOnStart;
            probes.LocalProxy = new Uri($"http://127.0.0.1:{proxy.Port}");
            AddLog($"Локальный HTTP CONNECT / SOCKS5: 127.0.0.1:{proxy.Port}. Выход: {InterfaceBox.Text}. Используйте отдельный браузер «Открыть сервисы». Уже открытые приложения этот прокси не используют.");
            ProxyLabel.Text = current.DesktopProxy ? $"Ваш браузер → 127.0.0.1:{proxy.Port} → {InterfaceBox.Text}. Telegram Desktop: кнопка подключения ниже. Discord должен быть исключён из VPN." : $"Локальный SOCKS5 / CONNECT · 127.0.0.1:{proxy.Port} · {InterfaceBox.Text}";
            runningTask = session.RunAsync(current, operation.Token);
            await runningTask;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { AddLog("Запрос прав администратора отменён."); }
        catch (Exception ex) { if (!closing) ShowError(ex); }
        finally { EndOperation(); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        openBrowserWhenReady = false;
        stopRequested = true;
        operation?.Cancel();
        try
        {
            engine.Stop();
            StopProxy();
            StopButton.IsEnabled = false;
            StateDetail.Text = "Останавливаем проверку и движок…";
            if (operation == null) ShowState(SessionState.Idle, "Обработка и локальный прокси выключены");
        }
        catch (Exception ex) { ShowError(ex); StopButton.IsEnabled = true; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { ReadConfig(explicitSave: true); AddLog("Настройки сохранены."); StateDetail.Text = proxy?.IsRunning == true ? "Настройки сохранены. Нажмите «Включить обработку», чтобы применить их к соединениям." : "Настройки сохранены. Можно проверить сеть или включить обработку."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Telegram_Click(object sender, RoutedEventArgs e)
    {
        if (proxy?.IsRunning != true) return;
        try
        {
            if (telegramBridge?.IsRunning != true) return;
            Process.Start(new ProcessStartInfo(telegramBridge.Link) { UseShellExecute = true });
            AddLog("Открыто предложение локального MTProxy в Telegram Desktop. Подтвердите его в Telegram. Транспорт: MTProto → WSS официального DC. При остановке «Связи» отключите локальный прокси в Telegram для прежнего маршрута.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Discord_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord");
            var executable = Directory.Exists(root) ? Directory.GetDirectories(root, "app-*").OrderDescending()
                .Select(path => Path.Combine(path, "Discord.exe")).FirstOrDefault(File.Exists) : null;
            if (executable == null) throw new IOException("Discord Desktop не найден.");
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void NativeCheck_Click(object sender, RoutedEventArgs e)
    {
        if (proxy?.IsRunning != true || nativeChecking) return;
        NativeCheckButton.IsEnabled = false;
        nativeChecking = true;
        NativeTelegramState.Text = NativeDiscordState.Text = "Проверяем транспорт…";
        try
        {
            var local = proxy;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var bridge = telegramBridge ?? throw new IOException("MTProxy выключен.");
            var telegram = TelegramProbe.CheckAsync(config, timeout.Token, connector: (_, _, token) =>
                DirectNetwork.ConnectAsync("127.0.0.1", bridge.Port, "", token), proxySecret: bridge.Secret);
            var discord = DiscordProbe.CheckAsync(new Uri($"http://127.0.0.1:{local.Port}"), timeout.Token);
            var voice = DiscordVoiceProbe.CheckRecentAsync(config.InterfaceId, timeout.Token);
            await Task.WhenAll(telegram, discord, voice);
            if (closing) return;
            NativeTelegramState.Text = telegram.Result.Detail;
            NativeDiscordState.Text = discord.Result.Detail + "\n" + voice.Result.Detail;
            File.WriteAllText(Path.Combine(dataDirectory, "native-probes.json"), JsonSerializer.Serialize(new[] { telegram.Result, discord.Result, voice.Result }, new JsonSerializerOptions { WriteIndented = true }));
            AddLog("[Telegram MTProto] " + telegram.Result.Detail);
            AddLog("[Discord Gateway] " + discord.Result.Detail);
            AddLog("[Discord UDP] " + voice.Result.Detail);
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                NativeTelegramState.Text = NativeDiscordState.Text = "Проверка не завершена; подробности в журнале.";
                AddLog("Проверка клиентов: " + ex.Message);
            }
        }
        finally { nativeChecking = false; if (!closing) NativeCheckButton.IsEnabled = proxy?.IsRunning == true; }
    }

    private async void ChatGptSetup_Click(object sender, RoutedEventArgs e)
    {
        if (chatGptSetupBusy) return;
        chatGptSetupBusy = true; ChatGptSetupButton.IsEnabled = false;
        NativeChatGptState.Text = "Настраиваем системный маршрут нативного клиента…";
        try
        {
            var result = await ChatGptDesktopIntegration.EnableAsync(updateLifetime.Token);
            if (closing) return;
            NativeChatGptState.Text = "Системный маршрут разрешён. Завершите текущую работу и полностью перезапустите ChatGPT. Доступ к модели нужно проверить в клиенте.";
            AddLog("[ChatGPT] Системный прокси нативного клиента разрешён. Нужен перезапуск ChatGPT." + (result.BackupPath != null ? " Резервная копия конфигурации сохранена рядом с исходным файлом." : ""));
        }
        catch (Exception ex) { if (!closing) NativeChatGptState.Text = "Настройка ChatGPT: " + ex.Message; }
        finally { chatGptSetupBusy = false; if (!closing) ChatGptSetupButton.IsEnabled = proxy?.IsRunning == true; }
    }

    private void UpdateLiveMetrics()
    {
        if (proxy is { IsRunning: false } && desktopProxy != null)
        {
            desktopProxy.Dispose(); desktopProxy = null; probes.LocalProxy = null;
            AddLog("Локальный прокси перестал принимать соединения. Предыдущие настройки Windows восстановлены; прямой DPI-движок продолжает работать.");
        }
        var active = proxy?.IsRunning == true;
        var elapsed = active && connectedAt.HasValue ? DateTimeOffset.Now - connectedAt.Value : TimeSpan.Zero;
        UptimeMetric.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        var traffic = proxy?.Traffic;
        var telegramTraffic = telegramBridge?.Traffic;
        ConnectionsMetric.Text = ((traffic?.ActiveConnections ?? 0) + (telegramTraffic?.ActiveConnections ?? 0)).ToString();
        ConnectionsMetric.ToolTip = $"Всего открыто за сеанс: {(traffic?.TotalConnections ?? 0) + (telegramTraffic?.TotalConnections ?? 0)} TCP-соединений";
        TrafficMetric.Text = FormatBytes((traffic?.SentBytes ?? 0) + (traffic?.ReceivedBytes ?? 0) + (telegramTraffic?.SentBytes ?? 0) + (telegramTraffic?.ReceivedBytes ?? 0));
        FailuresMetric.Text = failedChecks.ToString();
        NativeCheckButton.IsEnabled = active && !nativeChecking;
        TelegramButton.IsEnabled = active;
        ChatGptSetupButton.IsEnabled = active && !chatGptSetupBusy;
        DesktopStatus.Text = desktopProxy != null ? "СИСТЕМНЫЙ PAC · ВКЛЮЧЁН" : "СИСТЕМНЫЙ PAC · ВЫКЛЮЧЕН";
    }
    private static string FormatBytes(long value) => value >= 1048576 ? $"{value / 1048576d:F1} МБ" : $"{value / 1024d:F0} КБ";

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var name = ((System.Windows.Controls.Button)sender).Tag.ToString();
        var preset = ServiceCatalog.Presets.Single(p => p.Name == name);
        TargetsBox.Text = string.Join(Environment.NewLine, Lines(TargetsBox.Text).Concat(preset.Domains)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        StateDetail.Text = $"Добавлены домены {name}. Сохраните настройки перед использованием.";
    }

    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        if (proxy?.IsRunning != true) return;
        try
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe")
            };
            var browser = candidates.FirstOrDefault(File.Exists) ?? throw new IOException("Не найден Edge или Chrome.");
            var start = new ProcessStartInfo(browser) { UseShellExecute = false };
            foreach (var arg in new[] { "--no-first-run", "--no-default-browser-check", "--disable-quic",
                "--proxy-server=http://127.0.0.1:" + proxy.Port,
                "--user-data-dir=" + Path.Combine(dataDirectory, "browser-profile"), "--new-window",
                "https://chatgpt.com/", "https://discord.com/app", "https://www.youtube.com/", "https://web.telegram.org/a/" })
                start.ArgumentList.Add(arg);
            Process.Start(start)?.Dispose();
            AddLog("Открыт отдельный браузер через выбранный интерфейс. TLS-сертификаты не подменяются; вход в аккаунты выполняет пользователь.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JSON-отчёт (*.json)|*.json", FileName = $"svyaz-report-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new
            {
                version = CurrentVersion.ToString(), exportedAt = DateTimeOffset.Now, config, results,
                events = log.ToArray(), note = "HTTP/1.1 через выбранный интерфейс и настроенные маршруты. Во время сеанса пробы проходят через тот же локальный прокси, что и отдельный браузер. Редиректы и функции аккаунтов не проверяются."
            }, new JsonSerializerOptions { WriteIndented = true }));
            AddLog("Отчёт сохранён локально.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowResults(IReadOnlyList<ProbeResult> list)
    {
        results = list;
        EmptyHint.Visibility = Visibility.Collapsed;
        ResultsGrid.ItemsSource = list.Select(r => new
        {
            Host = new Uri(r.Url).Host,
            Role = r.IsCanary ? "Контрольный" : r.ViaExternalProxy ? "Внешний прокси" : "Прямой маршрут",
            Result = r.AccessNeedsBrowserCheck ? $"HTTP {r.StatusCode} · проверьте в браузере" :
                r.Healthy ? $"HTTP {r.StatusCode}" : r.Detail.StartsWith("Тайм-аут", StringComparison.Ordinal) ? "Тайм-аут" : r.Detail,
            Latency = r.StatusCode.HasValue ? $"{r.ElapsedMs} мс" : "—",
            FullDetail = r.Url + Environment.NewLine + r.Detail + Environment.NewLine + r.CheckedAt.ToString("HH:mm:ss")
        }).ToArray();
        var targets = list.Where(r => !r.IsCanary).ToArray();
        var canaries = list.Where(r => r.IsCanary).ToArray();
        TargetMetric.Text = $"{targets.Count(r => r.Healthy)} / {targets.Length}";
        CanaryMetric.Text = $"{canaries.Count(r => r.Healthy)} / {canaries.Length}";
        var times = list.Where(r => r.Healthy).Select(r => r.ElapsedMs).Order().ToArray();
        LatencyMetric.Text = times.Length == 0 ? "—" : $"{(times[(times.Length - 1) / 2] + times[times.Length / 2]) / 2} мс";
        LastCheck.Text = "Проверено в " + DateTime.Now.ToString("HH:mm:ss");
        failedChecks += list.Count(r => r.IsCanary && !r.Healthy);
        if (times.Length > 0)
        {
            latencyHistory.Enqueue(times[times.Length / 2]);
            while (latencyHistory.Count > 24) latencyHistory.Dequeue();
            var maximum = Math.Max(500, latencyHistory.Max());
            LatencyLine.Points = new PointCollection(latencyHistory.Select((value, index) => new Point(8 + index * 520d / 23, 54 - value * 44 / maximum)));
        }
        SaveStatus();
    }

    private void ShowState(SessionState state, string text)
    {
        ScanProgress.Visibility = state == SessionState.Checking ? Visibility.Visible : Visibility.Collapsed;
        StateDot.BeginAnimation(OpacityProperty, state is SessionState.Running or SessionState.Checking
            ? new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(1.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever } : null);
        lastState = state; lastMessage = text;
        var color = state switch
        {
            SessionState.Running => results.Where(r => !r.IsCanary).Any(r => !r.Healthy) ? "#FFC56F" : "#70E2BB",
            SessionState.Faulted or SessionState.RolledBack => "#FF7888",
            SessionState.Checking => "#FFC56F", _ => "#8294AF"
        };
        StateDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
        StateLabel.Text = state switch
        {
            SessionState.Running => "ОБРАБОТКА ВКЛЮЧЕНА", SessionState.Checking => "ПРОВЕРКА СЕТИ",
            SessionState.RolledBack => "АВТОМАТИЧЕСКИ ОСТАНОВЛЕНО", SessionState.Faulted => "ТРЕБУЕТСЯ ВНИМАНИЕ", _ => "ОБРАБОТКА ВЫКЛЮЧЕНА"
        };
        if (state == SessionState.Running && results.Where(r => !r.IsCanary).Any(r => !r.Healthy))
            StateLabel.Text = "ЗАПУЩЕНО · НЕ ВСЕ СЕРВИСЫ ПОДТВЕРЖДЕНЫ";
        StateTitle.Text = state switch
        {
            SessionState.Running => "Маршрут включён · проверяем приложения",
            SessionState.Checking => "Проверяем доступность сервисов",
            SessionState.RolledBack => "Потерян ранее доступный ответ",
            SessionState.Faulted => "Запуск не завершён", _ => "Прямое соединение без обработки"
        };
        StateDetail.Text = text;
        BrowserButton.IsEnabled = proxy?.IsRunning == true;
        if (state is SessionState.Faulted or SessionState.RolledBack && proxy?.IsRunning == true)
        {
            StateTitle.Text = "Обработка остановлена · локальный прокси работает";
            StateDetail.Text = text + ". Маршруты Telegram и ChatGPT остаются доступны через отдельный браузер, если внешние прокси отвечают.";
        }
        AddLog($"[состояние] {state}: {text}; движок={engine.IsRunning}; локальный прокси={proxy?.IsRunning == true}");
        SaveStatus();
        if (state == SessionState.Running && !nativeCheckLaunched)
        {
            nativeCheckLaunched = true;
            NativeCheck_Click(this, new RoutedEventArgs());
        }
        if (state == SessionState.Running && openBrowserWhenReady)
        {
            openBrowserWhenReady = false;
            Browser_Click(this, new RoutedEventArgs());
        }
    }

    private void ShowError(Exception ex) { ShowState(SessionState.Faulted, ex.Message); AddLog("Ошибка: " + ex.Message); }
    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = CheckButton.IsEnabled = SettingsPanel.IsEnabled = !busy;
        StopButton.IsEnabled = busy || engine.IsRunning || proxy?.IsRunning == true;
    }
    private void EndOperation()
    {
        if (stopRequested || closing) StopProxy();
        operation?.Dispose(); operation = null; runningTask = null;
        if (!closing)
        {
            SetBusy(false); BrowserButton.IsEnabled = proxy?.IsRunning == true;
            if (proxy?.IsRunning != true) ProxyLabel.Text = "Отдельный браузер использует выбранный интерфейс без смены системного маршрута";
            SaveStatus();
        }
    }
    private void StopProxy()
    {
        desktopProxy?.Dispose(); desktopProxy = null;
        telegramBridge?.Dispose(); telegramBridge = null;
        proxy?.Dispose(); proxy = null; probes.LocalProxy = null;
    }
    private void SaveStatus()
    {
        try { journal.Status(lastState, lastMessage, engine.IsRunning, proxy?.IsRunning == true, proxy?.Port ?? 0, results); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void OnUi(Action action)
    {
        if (closing) return;
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(() => { if (!closing) action(); });
    }
    private void AddLog(string text)
    {
        var line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
        log.Add(line);
        if (log.Count > 500) log.RemoveAt(0);
        LogBox.Text = string.Join(Environment.NewLine, log);
        LogBox.ScrollToEnd();
        try { journal.Append(text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        closing = true;
        StopUpdates();
        operation?.Cancel();
        StopProxy();
        engine.Dispose();
        lastState = SessionState.Idle; lastMessage = "Приложение закрыто"; SaveStatus();
    }
}
