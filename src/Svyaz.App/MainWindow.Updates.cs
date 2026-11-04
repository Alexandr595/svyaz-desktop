using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Svyaz.Core;

namespace Svyaz.App;

public partial class MainWindow
{
    private readonly ReleaseUpdateService updates = new();
    private readonly CancellationTokenSource updateLifetime = new();
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(4) };
    private ReleaseUpdate? availableUpdate;
    private CancellationTokenSource? updateDownload;
    private bool updateChecking;
    private static Version CurrentVersion
    {
        get { var value = typeof(MainWindow).Assembly.GetName().Version!; return new(value.Major, value.Minor, Math.Max(0, value.Build)); }
    }

    private void InitializeUpdates()
    {
        TitleVersion.Text = $"DESKTOP  {CurrentVersion}";
        SidebarVersion.Text = $"DESKTOP / {CurrentVersion}";
        AutoUpdatesBox.IsChecked = config.CheckForUpdates;
        PreviewUpdatesBox.IsChecked = config.PreviewUpdates;
        UpdateStatus.Text = $"Установлена версия {CurrentVersion}. Автоматическая проверка {(config.CheckForUpdates ? "включена" : "выключена")}.";
        Loaded += async (_, _) =>
        {
            updateTimer.Start();
            if (config.CheckForUpdates) await CheckUpdatesAsync(false);
        };
        updateTimer.Tick += async (_, _) => { if (config.CheckForUpdates) await CheckUpdatesAsync(false); };
        Closed += (_, _) => { updateLifetime.Dispose(); updates.Dispose(); };
    }

    private void StopUpdates()
    {
        updateTimer.Stop();
        updateLifetime.Cancel();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(true);

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (updateChecking || updateDownload != null || closing) return;
        updateChecking = true;
        CheckUpdatesButton.IsEnabled = AutoUpdatesBox.IsEnabled = PreviewUpdatesBox.IsEnabled = false;
        UpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = false;
        UpdateStatus.Text = "Проверяем выпуски на GitHub…";
        try
        {
            var previous = availableUpdate?.Version;
            var found = await updates.CheckAsync(CurrentVersion, config.PreviewUpdates, updateLifetime.Token);
            if (closing) return;
            availableUpdate = found;
            RenderUpdate();
            UpdateStatus.Text = found == null
                ? $"Установлена версия {CurrentVersion}. Доступных обновлений нет. Проверено в {DateTime.Now:HH:mm}."
                : $"Доступна версия {found.Version}{(found.Preview ? " · предварительный выпуск" : "")}. Установлена {CurrentVersion}. Размер: {found.Size / 1024d / 1024:0.#} МБ.";
            if (found != null && previous != found.Version) AddLog($"Доступно обновление «Связи» до {found.Version}. Установка — по кнопке вверху окна.");
        }
        catch (OperationCanceledException) when (updateLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (closing) return;
            UpdateStatus.Text = "Не удалось проверить обновления. Соединения продолжают работать; можно повторить позже.";
            if (manual) AddLog("Проверка обновлений: " + ex.Message);
        }
        finally
        {
            updateChecking = false;
            if (!closing)
            {
                CheckUpdatesButton.IsEnabled = AutoUpdatesBox.IsEnabled = PreviewUpdatesBox.IsEnabled = updateDownload == null;
                UpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = true;
            }
        }
    }

    private void RenderUpdate()
    {
        var visible = availableUpdate != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Visibility = InstallUpdateButton.Visibility = visible;
        if (availableUpdate == null) return;
        UpdateButton.Content = $"↥ Доступно {availableUpdate.Version}";
        UpdateButton.ToolTip = $"Скачать и установить {availableUpdate.Version}{(availableUpdate.Preview ? " (предварительный выпуск)" : "")}. Соединения остановятся, приложение перезапустится. Настройки сохранятся.";
        InstallUpdateButton.Content = $"Обновить до {availableUpdate.Version}";
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (updateChecking || closing) return;
        if (updateDownload != null) { updateDownload.Cancel(); return; }
        var release = availableUpdate;
        if (release == null) { await CheckUpdatesAsync(true); return; }
        using var download = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        updateDownload = download;
        CheckUpdatesButton.IsEnabled = AutoUpdatesBox.IsEnabled = PreviewUpdatesBox.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        InstallUpdateButton.Content = "Отменить загрузку";
        UpdateStatus.Text = "Загружаем обновление. Текущие соединения продолжают работать.";
        var progress = new Progress<int>(value =>
        {
            if (closing || updateDownload != download) return;
            UpdateProgress.Value = value;
            UpdateButton.Content = $"Загрузка {value}% · отмена";
        });
        try
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Svyaz", "Updates");
            var installer = await updates.DownloadAsync(release, cache, progress, download.Token);
            download.Token.ThrowIfCancellationRequested();
            if (closing) return;
            UpdateStatus.Text = "SHA-256 проверен. Запускаем установку и перезапускаем приложение…";
            var command = new ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installer)!,
                Arguments = UpdateInstallerCommand.Arguments(AppContext.BaseDirectory)
            };
            using var setup = Process.Start(command) ?? throw new IOException("Windows не запустила установщик.");
            AddLog($"Обновление {release.Version}: SHA-256 проверен. Останавливаем соединения для установки.");
            Close();
        }
        catch (OperationCanceledException)
        {
            if (!closing) UpdateStatus.Text = download.IsCancellationRequested ? "Загрузка отменена. Текущая версия продолжает работать." : "Срок загрузки истёк. Можно повторить обновление.";
        }
        catch (Exception ex)
        {
            if (!closing) { UpdateStatus.Text = "Обновление не установлено: " + ex.Message; AddLog("Ошибка обновления: " + ex.Message); }
        }
        finally
        {
            updateDownload = null;
            if (!closing)
            {
                UpdateProgress.Visibility = Visibility.Collapsed;
                CheckUpdatesButton.IsEnabled = AutoUpdatesBox.IsEnabled = PreviewUpdatesBox.IsEnabled = !updateChecking;
                RenderUpdate();
            }
        }
    }

    private async void UpdateSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (configError) throw new IOException("Сначала исправьте и сохраните настройки приложения.");
            var next = config with { CheckForUpdates = AutoUpdatesBox.IsChecked == true, PreviewUpdates = PreviewUpdatesBox.IsChecked == true };
            next.Save(ConfigPath);
            config = next;
            if (!config.PreviewUpdates && availableUpdate?.Preview == true) { availableUpdate = null; RenderUpdate(); }
            if (config.CheckForUpdates) await CheckUpdatesAsync(false);
            else UpdateStatus.Text = "Автоматическая проверка выключена. Можно проверить обновления вручную.";
        }
        catch (Exception ex)
        {
            AutoUpdatesBox.IsChecked = config.CheckForUpdates;
            PreviewUpdatesBox.IsChecked = config.PreviewUpdates;
            UpdateStatus.Text = "Не удалось сохранить настройку обновлений: " + ex.Message;
        }
    }

    private void Releases_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo($"https://github.com/{ReleaseUpdateService.Repository}/releases") { UseShellExecute = true }); }
        catch (Exception ex) { UpdateStatus.Text = "Не удалось открыть браузер: " + ex.Message; }
    }
}
