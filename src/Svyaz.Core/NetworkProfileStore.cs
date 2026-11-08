using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Svyaz.Core;

public sealed record SavedNetworkProfiles(string Key, DateTimeOffset CheckedAt, Dictionary<string, string> Profiles);

public sealed class NetworkProfileStore(string path)
{
    public static string Key(AppConfig config, string signature)
    {
        var scope = JsonSerializer.Serialize(new { config.InterfaceId, config.NetworkName, signature,
            Targets = config.Targets.Order().ToArray(), Canaries = config.Canaries.Order().ToArray(),
            Engine = ZapretEngine.Hashes["winws2.exe"], config.Profile });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    }
    public static string Signature(string interfaceId)
    {
        var network = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n => n.Id == interfaceId)
            ?? throw new IOException("Выбранный интерфейс недоступен.");
        var properties = network.GetIPProperties();
        return string.Join('|', properties.GatewayAddresses.Select(g => g.Address.ToString()).Order()) + ";" +
            string.Join('|', properties.UnicastAddresses.Select(a => a.Address + "/" + a.PrefixLength).Order());
    }
    public AppConfig? Find(AppConfig config, string signature)
    {
        var match = Read().FirstOrDefault(r => r.Key == Key(config, signature) && DateTimeOffset.UtcNow - r.CheckedAt < TimeSpan.FromDays(14));
        return match == null ? null : (config with { ServiceProfiles = new(match.Profiles) }).Validate();
    }
    public void Save(AppConfig config, string signature)
    {
        config = config.Validate();
        var key = Key(config, signature);
        var rows = Read().Where(r => r.Key != key).Prepend(new SavedNetworkProfiles(key, DateTimeOffset.UtcNow, new(config.ServiceProfiles))).Take(32).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
    public void Remove(AppConfig config, string signature)
    {
        if (!File.Exists(path)) return;
        var remaining = Read().Where(r => r.Key != Key(config, signature)).ToArray();
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(remaining));
        File.Move(temp, path, true);
    }
    private SavedNetworkProfiles[] Read()
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 128 * 1024) throw new IOException("Хранилище сетевых профилей слишком большое.");
        var rows = JsonSerializer.Deserialize<SavedNetworkProfiles[]>(File.ReadAllText(path)) ?? throw new IOException("Пустое хранилище сетевых профилей.");
        if (rows.Length > 32 || rows.Any(r => r == null || r.Key == null || r.Key.Length != 64 || r.Profiles == null || r.Profiles.Count > 4 ||
            r.Profiles.Any(p => !ServiceStrategies.Services.Contains(p.Key) || !ServiceStrategies.IsProfile(p.Value))))
            throw new IOException("Хранилище сетевых профилей повреждено.");
        return rows;
    }
}
