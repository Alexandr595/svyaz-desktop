using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Svyaz.Core;

public sealed class GoodbyeEngine(string engineDirectory, string dataDirectory) : IEngine
{
    public static readonly IReadOnlyDictionary<string, string> ExpectedHashes = new Dictionary<string, string>
    {
        ["goodbyedpi.exe"] = "8D412B094BB9C137FF25BA9A794D1122ECC84BB776DEBFF6C249723A13CC31CD",
        ["WinDivert.dll"] = "6110BFA44667405179C3E15E12AF1B62037E447ED59B054B19042032995E6C7E",
        ["WinDivert64.sys"] = "E69B5BA3F0CD6CFB2983E442636E7F0B342B61B15264B0328317D4559C82CF50"
    };
    private Process? process;
    private WindowsJob? job;
    public event Action<string>? Output;
    public bool IsRunning => process is { HasExited: false };
    public int? ExitCode => process is { HasExited: true } ? process.ExitCode : null;

    public static void VerifyBundle(string directory)
    {
        foreach (var (name, hash) in ExpectedHashes)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new FileNotFoundException($"Нет файла движка {name}. Распакуйте полный архив приложения.");
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Проверка SHA-256 не пройдена: {name}. Запуск отменён.");
        }
    }

    public static string[] Arguments(string profile, string hostList) => profile switch
    {
        "gentle" => ["-e", "2", "--native-frag", "--reverse-frag", "--blacklist", hostList],
        "sni" => ["-e", "1", "--frag-by-sni", "--native-frag", "--reverse-frag", "--blacklist", hostList],
        _ => throw new ArgumentException("Неизвестный профиль.")
    };

    public void Start(AppConfig config)
    {
        config = config.Validate();
        if (IsRunning) throw new InvalidOperationException("Движок уже запущен.");
        VerifyBundle(engineDirectory);
        foreach (var name in new[] { "goodbyedpi", "winws", "winws2" })
        {
            var existing = Process.GetProcessesByName(name);
            try
            {
                if (existing.Length != 0)
                    throw new InvalidOperationException($"Уже работает {name}. Остановите его перед запуском «Связи».");
            }
            finally { foreach (var item in existing) item.Dispose(); }
        }
        Directory.CreateDirectory(dataDirectory);
        var hostList = Path.GetFullPath(Path.Combine(dataDirectory, "active-hosts.txt"));
        File.WriteAllLines(hostList, config.Targets, new UTF8Encoding(false));
        var start = new ProcessStartInfo(Path.Combine(engineDirectory, "goodbyedpi.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = engineDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in Arguments(config.Profile, hostList)) start.ArgumentList.Add(argument);
        job = new WindowsJob();
        try
        {
            // Assign only after Start succeeds: HasExited on an unstarted Process would
            // hide the original startup error (for example, missing elevation).
            process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить GoodbyeDPI.");
            process.OutputDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data); };
            job.Assign(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch { Stop(); throw; }
    }

    public void Stop()
    {
        var current = process;
        if (current != null && !current.HasExited)
        {
            current.Kill(entireProcessTree: true);
            if (!current.WaitForExit(3000)) throw new IOException("Процесс GoodbyeDPI ещё работает.");
        }
        job?.Dispose();
        job = null;
        current?.Dispose();
        process = null;
    }

    public void Dispose() { try { Stop(); } finally { job?.Dispose(); } }
}
