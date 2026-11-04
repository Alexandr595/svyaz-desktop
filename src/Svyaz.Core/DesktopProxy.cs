using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Svyaz.Core;

public static class DesktopPac
{
    public static string Create(IEnumerable<string> domains, int port, string otherTraffic = "DIRECT")
    {
        if (port is < 1024 or > 65535) throw new ArgumentException("Некорректный порт PAC.");
        var list = domains.Select(AppConfig.NormalizeDomain).Distinct().ToArray();
        return "function FindProxyForURL(url, host) {\n" +
            "  host = host.toLowerCase();\n  var domains = " + JsonSerializer.Serialize(list) + ";\n" +
            "  for (var i = 0; i < domains.length; i++) {\n" +
            "    if (host === domains[i] || host.slice(-(domains[i].length + 1)) === '.' + domains[i])\n" +
            "      return 'PROXY 127.0.0.1:" + port + "';\n  }\n" +
            "  return " + JsonSerializer.Serialize(otherTraffic) + ";\n}\n";
    }
}

public sealed record InternetProxyState(string? AutoConfigUrl, int? ProxyEnable, int? ConnectionFlags = null);
public interface IInternetProxySettings
{
    InternetProxyState Read();
    void Write(InternetProxyState state);
}

// Owns just the two changed values. Never overwrites settings edited by another app.
public sealed class DesktopProxyLease : IDisposable
{
    private sealed record Backup(InternetProxyState Before, string InstalledUrl, int OwnerPid);
    private readonly string path;
    private readonly IInternetProxySettings settings;
    public string Url { get; }
    private bool disposed;
    public DesktopProxyLease(string directory, int port, IInternetProxySettings settings)
    {
        this.settings = settings;
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "desktop-proxy-backup.json");
        if (File.Exists(path)) throw new IOException("Осталась копия системного прокси. Сначала восстановите прежние настройки.");
        var before = settings.Read();
        if (!string.IsNullOrWhiteSpace(before.AutoConfigUrl) || before.ProxyEnable == 1)
            throw new InvalidOperationException("В Windows уже настроен другой прокси/PAC. Сначала отключите режим системного прокси в его приложении; активный TUN можно оставить.");
        Url = $"http://127.0.0.1:{port}/proxy.pac?session={Guid.NewGuid():N}";
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new Backup(before, Url, Environment.ProcessId)));
        File.Move(path + ".tmp", path, true);
        try { settings.Write(Installed(before, Url)); }
        catch { settings.Write(before); File.Delete(path); throw; }
    }
    public static bool Restore(string directory, IInternetProxySettings settings, int? ownerPid = null)
    {
        var path = Path.Combine(directory, "desktop-proxy-backup.json");
        if (!File.Exists(path)) return false;
        var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(path)) ?? throw new IOException("Не удалось прочитать копию настроек прокси.");
        if (ownerPid.HasValue && backup.OwnerPid != ownerPid.Value) return false;
        if (settings.Read() == Installed(backup.Before, backup.InstalledUrl)) settings.Write(backup.Before);
        File.Delete(path);
        return true;
    }
    public void Dispose()
    {
        if (disposed) return;
        Restore(Path.GetDirectoryName(path)!, settings, Environment.ProcessId);
        disposed = true;
    }
    private static InternetProxyState Installed(InternetProxyState before, string url) =>
        new(url, 0, before.ConnectionFlags.HasValue ? (before.ConnectionFlags.Value & 8) | 5 : null);
}

public sealed class WindowsInternetProxySettings : IInternetProxySettings
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    public InternetProxyState Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return new InternetProxyState(key?.GetValue("AutoConfigURL") as string, key?.GetValue("ProxyEnable") as int?, ReadFlags());
    }
    public void Write(InternetProxyState state)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ApplyConnection(state);
        using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
        if (state.AutoConfigUrl == null) key.DeleteValue("AutoConfigURL", false);
        else key.SetValue("AutoConfigURL", state.AutoConfigUrl, RegistryValueKind.String);
        if (state.ProxyEnable == null) key.DeleteValue("ProxyEnable", false);
        else key.SetValue("ProxyEnable", state.ProxyEnable.Value, RegistryValueKind.DWord);
        _ = InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }
    private static int ReadFlags()
    {
        var option = Marshal.AllocHGlobal(Marshal.SizeOf<ConnectionOption>());
        var list = Marshal.AllocHGlobal(Marshal.SizeOf<ConnectionOptions>());
        try
        {
            Marshal.StructureToPtr(new ConnectionOption { Option = 1 }, option, false);
            var options = new ConnectionOptions { Size = Marshal.SizeOf<ConnectionOptions>(), Count = 1, Options = option };
            Marshal.StructureToPtr(options, list, false);
            var length = options.Size;
            if (!InternetQueryOption(IntPtr.Zero, 75, list, ref length)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<ConnectionOption>(option).Value.ToInt32();
        }
        finally { Marshal.FreeHGlobal(list); Marshal.FreeHGlobal(option); }
    }
    private static void ApplyConnection(InternetProxyState state)
    {
        var stride = Marshal.SizeOf<ConnectionOption>();
        var option = Marshal.AllocHGlobal(stride * 2);
        var list = Marshal.AllocHGlobal(Marshal.SizeOf<ConnectionOptions>());
        var url = Marshal.StringToHGlobalUni(state.AutoConfigUrl ?? "");
        try
        {
            var flags = state.ConnectionFlags ?? (1 | (state.ProxyEnable == 1 ? 2 : 0) | (string.IsNullOrEmpty(state.AutoConfigUrl) ? 0 : 4));
            Marshal.StructureToPtr(new ConnectionOption { Option = 1, Value = new IntPtr(flags) }, option, false);
            Marshal.StructureToPtr(new ConnectionOption { Option = 4, Value = url }, option + stride, false);
            var options = new ConnectionOptions { Size = Marshal.SizeOf<ConnectionOptions>(), Count = 2, Options = option };
            Marshal.StructureToPtr(options, list, false);
            if (!InternetSetOption(IntPtr.Zero, 75, list, options.Size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(url); Marshal.FreeHGlobal(list); Marshal.FreeHGlobal(option); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectionOption { public int Option; public IntPtr Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectionOptions { public int Size; public IntPtr Connection; public int Count; public int Error; public IntPtr Options; }
    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "InternetQueryOptionW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetQueryOption(IntPtr internet, int option, IntPtr buffer, ref int length);
    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "InternetSetOptionW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);
}
