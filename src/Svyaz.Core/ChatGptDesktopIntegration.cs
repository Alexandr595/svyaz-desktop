using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Svyaz.Core;

public sealed record ChatGptProxySetup(bool Changed, string? BackupPath);

// Let the vendor's TOML-aware CLI change its own single feature flag. Never
// rewrite the user's configuration with a custom parser or change approvals.
public static class ChatGptDesktopIntegration
{
    public static string? FindCli()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (!Directory.Exists(root)) return null;
        return new DirectoryInfo(root).EnumerateDirectories()
            .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).OrderByDescending(d => d.LastWriteTimeUtc)
            .Select(d => Path.Combine(d.FullName, "codex.exe")).FirstOrDefault(File.Exists);
    }

    public static async Task<bool> IsEnabledAsync(CancellationToken token)
    {
        var cli = FindCli() ?? throw new IOException("Встроенный компонент нового ChatGPT не найден. Откройте ChatGPT хотя бы один раз.");
        var text = await RunAsync(cli, ["features", "list"], token).ConfigureAwait(false);
        var line = text.Split('\n').FirstOrDefault(s => Regex.IsMatch(s, @"^respect_system_proxy\s", RegexOptions.CultureInvariant));
        if (line == null) throw new IOException("Эта версия ChatGPT не поддерживает системный маршрут. Обновите ChatGPT.");
        return Regex.IsMatch(line.TrimEnd(), @"\s+true$", RegexOptions.CultureInvariant);
    }

    public static async Task<ChatGptProxySetup> EnableAsync(CancellationToken token)
    {
        if (await IsEnabledAsync(token).ConfigureAwait(false)) return new(false, null);
        var cli = FindCli()!;
        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var directory = string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : configuredHome;
        var config = Path.Combine(directory, "config.toml");
        string? backup = null;
        if (File.Exists(config))
        {
            backup = config + ".svyaz-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
            File.Copy(config, backup, overwrite: false);
        }
        await RunAsync(cli, ["features", "enable", "respect_system_proxy"], token).ConfigureAwait(false);
        if (!await IsEnabledAsync(token).ConfigureAwait(false)) throw new IOException("ChatGPT не подтвердил включение системного маршрута.");
        return new(true, backup);
    }

    private static async Task<string> RunAsync(string cli, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить компонент настройки ChatGPT.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            _ = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Компонент ChatGPT не применил настройку. Проверьте его конфигурацию.");
            return text;
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            throw;
        }
    }
}
