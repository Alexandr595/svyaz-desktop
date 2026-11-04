using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Svyaz.Core;

public sealed record AppConfig
{
    public int SchemaVersion { get; init; } = 2;
    public string[] Targets { get; init; } = ServiceCatalog.AllDomains;
    public string[] Canaries { get; init; } = ["https://example.com/", "https://www.microsoft.com/"];
    public string Profile { get; init; } = "z-fake-md5";
    public string NetworkName { get; init; } = "Не указан";
    public string InterfaceId { get; init; } = "";
    public string TelegramProxy { get; init; } = "socks5://212.77.75.25:1088";
    public string[] TelegramProxyBackups { get; init; } = ["socks5://47.90.162.114:5555", "socks5://81.162.123.152:10808"];
    public int LocalProxyPort { get; init; } = 18080;
    public int TelegramLocalPort { get; init; } = 18081;
    public bool OpenBrowserOnStart { get; init; } = false;
    public bool DesktopProxy { get; init; } = true;
    public bool DiscordVoice { get; init; } = false;
    public bool GlassAppearance { get; init; } = true;
    public bool CheckForUpdates { get; init; } = true;
    public bool PreviewUpdates { get; init; } = true;
    public string ChatGptProxy { get; init; } = "";
    public string[] ChatGptProxyBackups { get; init; } = [];
    public int IntervalSeconds { get; init; } = 30;
    public int TimeoutSeconds { get; init; } = 8;

    public AppConfig Validate()
    {
        if (SchemaVersion != 2) throw new ArgumentException("Неподдерживаемая версия настроек. Обновите приложение.");
        if (Targets is null || Targets.Length is < 1 or > 32)
            throw new ArgumentException("Укажите от 1 до 32 доменов для обработки.");
        if (Canaries is null || Canaries.Length is < 1 or > 32)
            throw new ArgumentException("Укажите от 1 до 32 контрольных HTTPS-адресов.");
        if (Profile is not ("gentle" or "sni") && !ZapretEngine.Profiles.Any(p => p.Id == Profile)) throw new ArgumentException("Неизвестный профиль.");
        if (string.IsNullOrWhiteSpace(NetworkName) || NetworkName.Length > 80 || NetworkName.Any(char.IsControl))
            throw new ArgumentException("Название сети: от 1 до 80 символов без перевода строки.");
        if (InterfaceId is null || InterfaceId.Length > 100 || InterfaceId.Any(char.IsControl))
            throw new ArgumentException("Некорректный идентификатор интерфейса.");
        if (TelegramProxy is null) throw new ArgumentException("Некорректный прокси Telegram.");
        _ = TelegramRoute.ParseProxy(TelegramProxy);
        if (TelegramProxyBackups is null || TelegramProxyBackups.Length > 5) throw new ArgumentException("Не более пяти резервных прокси Telegram.");
        foreach (var backup in TelegramProxyBackups) _ = TelegramRoute.ParseProxy(backup);
        if (LocalProxyPort is < 1024 or > 65535) throw new ArgumentException("Порт локального прокси: 1024–65535.");
        if (TelegramLocalPort is < 1024 or > 65535 || TelegramLocalPort == LocalProxyPort) throw new ArgumentException("MTProxy Telegram должен иметь отдельный порт 1024–65535.");
        if (ChatGptProxy is null || ChatGptProxyBackups is null || ChatGptProxyBackups.Length > 5) throw new ArgumentException("Некорректные прокси ChatGPT.");
        _ = TelegramRoute.ParseProxy(ChatGptProxy);
        foreach (var backup in ChatGptProxyBackups) _ = TelegramRoute.ParseProxy(backup);
        if (IntervalSeconds is < 10 or > 600 || TimeoutSeconds is < 2 or > 30)
            throw new ArgumentException("Интервал: 10–600 секунд; тайм-аут: 2–30 секунд.");
        var targets = Targets.Select(NormalizeDomain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var canaries = Canaries.Select(NormalizeEndpoint).Distinct().ToArray();
        foreach (var endpoint in canaries)
        {
            var host = new Uri(endpoint).IdnHost;
            if (targets.Any(t => host.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                                host.EndsWith("." + t, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Контрольный адрес {host} входит в список обработки. Выберите независимый сервис.");
        }
        return this with { Targets = targets, Canaries = canaries };
    }

    public static string NormalizeDomain(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Пустой домен.");
        var value = input.Trim().TrimEnd('.');
        if (value.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("В списке обработки нужны домены или HTTPS-адреса без пути, порт 443.");
            value = uri.IdnHost;
        }
        try { value = new IdnMapping().GetAscii(value).ToLowerInvariant(); }
        catch (ArgumentException) { throw new ArgumentException($"Некорректный домен: {input}"); }
        if (value.Length > 253 || !value.Contains('.') || IPAddress.TryParse(value, out _) ||
            Uri.CheckHostName(value) != UriHostNameType.Dns ||
            value.Split('.').Any(label => label.Length is < 1 or > 63 || label[0] == '-' ||
                label[^1] == '-' || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))))
            throw new ArgumentException($"Некорректный домен: {input}");
        return value;
    }

    public static string NormalizeEndpoint(string input)
    {
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("Контрольные адреса должны начинаться с https://, без логина и фрагмента #.");
        return uri.AbsoluteUri;
    }

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) return new AppConfig().Validate();
        var text = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppConfig>(text) ?? throw new ArgumentException("Пустой конфиг.");
        using var document = JsonDocument.Parse(text);
        // 0.4.1 enabled experimental UDP manipulation by default. Preserve the
        // rest of the configuration, but require an explicit choice after upgrade.
        if (!document.RootElement.TryGetProperty(nameof(SchemaVersion), out _)) config = config with { DiscordVoice = false, SchemaVersion = 2 };
        return config.Validate();
    }

    public void Save(string path)
    {
        var validated = Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(validated, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
