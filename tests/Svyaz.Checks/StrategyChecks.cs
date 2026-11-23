using System.Net;
using Svyaz.Core;

static class StrategyChecks
{
    static AppConfig Config => new() { InterfaceId = "test-interface", Targets = ["discord.com", "discord.gg", "youtube.com"], TelegramProxy = "", TelegramProxyBackups = [] };
    static StrategySnapshot Snapshot(bool discord, bool youtube, bool controls = true, int? extraStatus = null) => new(
        new[] { new ProbeResult("https://example.com/", true, controls, controls ? 200 : null, 1, "fixture", DateTimeOffset.Now) }
            .Concat(extraStatus == null ? [] : new[] { new ProbeResult("https://chatgpt.com/", false, false, extraStatus, 1, "fixture", DateTimeOffset.Now) }).ToArray(),
        [new("Discord", discord, "fixture"), new("YouTube", youtube, "fixture")]);
    static StrategyTuner Tuner(IStrategyProbe probes, IEngine engine) => new(probes, engine) { StartupDelay = TimeSpan.Zero, Candidates = ["z-split", "z-disorder"] };
    static void Assert(bool ok) { if (!ok) throw new Exception("Strategy assertion failed"); }
    public static (string Name, Func<Task> Test)[] Cases() =>
    [
        ("New settings have no bundled public proxy dependencies", () =>
        {
            var config = new AppConfig().Validate();
            Assert(config.TelegramProxy == "" && config.ChatGptProxy == "" && config.TelegramProxyBackups.Length == 0 && config.ChatGptProxyBackups.Length == 0);
            return Task.CompletedTask;
        }),
        ("Tuning composes different service winners and verifies their combination", async () =>
        {
            using var engine = new TestEngine();
            var probes = new TestProbes(c => !engine.IsRunning ? Snapshot(false, false) :
                c.ServiceProfiles.Count == 2 ? Snapshot(true, true) : c.Profile == "z-split" ? Snapshot(true, false) :
                c.Profile == "z-disorder" ? Snapshot(false, true) : Snapshot(false, false));
            var result = await Tuner(probes, engine).RunAsync(Config, CancellationToken.None);
            Assert(result.Verified && result.Config.ServiceProfiles["Discord"] == "z-split" && result.Config.ServiceProfiles["YouTube"] == "z-disorder");
            Assert(!engine.IsRunning && engine.Starts == engine.Stops);
        }),
        ("An intermittent service result cannot become a selected profile", async () =>
        {
            using var engine = new TestEngine(); var calls = 0;
            var probes = new TestProbes(_ => Snapshot(engine.IsRunning && ++calls % 2 == 1, false));
            var result = await Tuner(probes, engine).RunAsync(Config, CancellationToken.None);
            Assert(!result.Verified && !result.Changed && result.Config.ServiceProfiles.Count == 0 && !engine.IsRunning);
        }),
        ("A control failure aborts tuning and retains the previous configuration", async () =>
        {
            using var engine = new TestEngine();
            var probes = new TestProbes(c => Snapshot(false, false, !engine.IsRunning || c.Profile != "z-split"));
            var result = await Tuner(probes, engine).RunAsync(Config, CancellationToken.None);
            Assert(!result.Verified && result.Config.ServiceProfiles.Count == 0 && !engine.IsRunning);
        }),
        ("The combined service profiles must preserve an earlier HTTPS 403", async () =>
        {
            using var engine = new TestEngine();
            var probes = new TestProbes(c => c.ServiceProfiles.Count > 0 ? Snapshot(true, true) : Snapshot(engine.IsRunning, engine.IsRunning, extraStatus: 403));
            var original = Config;
            var result = await Tuner(probes, engine).RunAsync(original, CancellationToken.None);
            Assert(!result.Verified && result.Config.ServiceProfiles.Count == 0 && !engine.IsRunning);
        }),
        ("Cancelled tuning leaves no running engine or saved selection", async () =>
        {
            using var engine = new TestEngine(); using var cancel = new CancellationTokenSource();
            var probes = new TestProbes(_ => { if (engine.IsRunning) cancel.Cancel(); return Snapshot(false, false); });
            try { await Tuner(probes, engine).RunAsync(Config, cancel.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            Assert(!engine.IsRunning && engine.Starts == engine.Stops);
        }),
        ("Tuning never sends a probe through external proxies or changes UDP settings", async () =>
        {
            using var engine = new TestEngine();
            var original = Config with { TelegramProxy = "socks5://proxy.example:1080", ChatGptProxy = "socks5://other.example:1080", DiscordVoice = true };
            var probes = new TestProbes(c => { Assert(c.TelegramProxy == "" && c.ChatGptProxy == "" && !c.DiscordVoice && !c.DesktopProxy); return Snapshot(true, true); });
            var result = await Tuner(probes, engine).RunAsync(original, CancellationToken.None);
            Assert(result.Verified && result.Config.TelegramProxy == original.TelegramProxy && result.Config.DiscordVoice);
            Assert(engine.Configs.All(c => c.TelegramProxy == original.TelegramProxy && !c.DiscordVoice));
        }),
        ("Service-specific rules stay inside selected domains and precede the fallback", () =>
        {
            var config = Config with { InterfaceId = "", ServiceProfiles = new() { ["Discord"] = "z-disorder", ["YouTube"] = "direct" } };
            var arguments = ZapretEngine.Arguments(config.Validate(), "hosts", "exclude");
            Assert(Array.IndexOf(arguments, "--hostlist-domains=discord.com,discord.gg") < Array.IndexOf(arguments, "--hostlist=hosts"));
            Assert(arguments.Contains("--hostlist-domains=youtube.com") && arguments.Contains("--lua-desync=pass"));
            Assert(!arguments.Any(a => a.StartsWith("--wf-udp")) && arguments.Count(a => a == "--hostlist-exclude=exclude") == 3);
            try { (config with { ServiceProfiles = new() { ["bad"] = "z-split" } }).Validate(); throw new Exception("Accepted unknown scope"); }
            catch (ArgumentException) { }
            return Task.CompletedTask;
        }),
        ("Network profiles are isolated by network name, interface, scope and signature", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "svyaz-network-" + Guid.NewGuid() + ".json");
            try
            {
                var store = new NetworkProfileStore(path); var config = Config with { ServiceProfiles = new() { ["Discord"] = "z-split" } };
                store.Save(config, "gateway-a");
                Assert(store.Find(Config, "gateway-a")?.ServiceProfiles["Discord"] == "z-split");
                Assert(store.Find(Config, "gateway-b") == null && store.Find(Config with { NetworkName = "another" }, "gateway-a") == null);
                Assert(store.Find(Config with { Targets = ["youtube.com"] }, "gateway-a") == null);
                Assert(!File.ReadAllText(path).Contains("proxy.example"));
                store.Remove(config, "gateway-a"); Assert(store.Find(config, "gateway-a") == null);
                File.WriteAllText(path, "invalid json");
                try { store.Find(config, "gateway-a"); throw new Exception("Accepted corrupted cache"); } catch (System.Text.Json.JsonException) { }
            }
            finally { File.Delete(path); }
            return Task.CompletedTask;
        }),
        ("Control failures never call automatic strategy repair", async () =>
        {
            using var engine = new TestEngine(); var n = 0; var repairs = 0;
            var controller = new SessionController(new HttpProbes(() => ++n == 1), engine) { InitialDelay = TimeSpan.Zero, IntervalOverride = TimeSpan.Zero,
                RepairAsync = (_, _) => { repairs++; return Task.FromResult<AppConfig?>(Config); } };
            await controller.RunAsync(Config, CancellationToken.None); Assert(repairs == 0 && !engine.IsRunning);
        }),
        ("Two direct service failures permit only one repair and recheck the result", async () =>
        {
            using var engine = new TestEngine(); var n = 0; var repairs = 0;
            var controller = new SessionController(new HttpProbes(() => true, () => ++n is 1 or 4 or 5 or 6 or 7), engine)
            { InitialDelay = TimeSpan.Zero, IntervalOverride = TimeSpan.Zero,
                RepairAsync = (c, _) => { Assert(!engine.IsRunning); repairs++; return Task.FromResult<AppConfig?>(c); } };
            await controller.RunAsync(Config, CancellationToken.None); Assert(repairs == 1 && !engine.IsRunning);
        }),
        ("Repair keeps other services running when the failed endpoint also fails without the engine", async () =>
        {
            using var engine = new TestEngine(); using var cancel = new CancellationTokenSource();
            var n = 0; var repairs = 0; var running = 0;
            var probes = new HttpProbes(() => true, () =>
            {
                n++;
                if (n is 4 or 5) Assert(!engine.IsRunning);
                return n == 1;
            });
            var controller = new SessionController(probes, engine)
            {
                InitialDelay = TimeSpan.Zero, IntervalOverride = TimeSpan.Zero,
                RepairAsync = (c, _) => { Assert(n == 5 && !engine.IsRunning); repairs++; return Task.FromResult<AppConfig?>(c); }
            };
            controller.StateChanged += (state, _) => { if (state == SessionState.Running && engine.Starts == 2) { running++; cancel.Cancel(); } };
            await controller.RunAsync(Config, cancel.Token);
            Assert(repairs == 1 && running == 1 && engine.Starts == engine.Stops);
        }),
        ("Repair still rolls back when an endpoint answers without the engine but fails with it", async () =>
        {
            using var engine = new TestEngine(); var repairs = 0; var terminal = SessionState.Idle;
            var controller = new SessionController(new HttpProbes(() => true, () => !engine.IsRunning), engine)
            {
                InitialDelay = TimeSpan.Zero, IntervalOverride = TimeSpan.Zero,
                RepairAsync = (c, _) => { repairs++; return Task.FromResult<AppConfig?>(c); }
            };
            controller.StateChanged += (state, _) => terminal = state;
            await controller.RunAsync(Config, CancellationToken.None);
            Assert(repairs == 1 && terminal == SessionState.RolledBack && !engine.IsRunning);
        }),
        ("A failed control in the stopped-engine baseline prevents strategy repair", async () =>
        {
            using var engine = new TestEngine(); var n = 0; var repairs = 0; var terminal = SessionState.Idle;
            var controller = new SessionController(new HttpProbes(() => ++n != 4, () => n == 1), engine)
            {
                InitialDelay = TimeSpan.Zero, IntervalOverride = TimeSpan.Zero,
                RepairAsync = (c, _) => { repairs++; return Task.FromResult<AppConfig?>(c); }
            };
            controller.StateChanged += (state, _) => terminal = state;
            await controller.RunAsync(Config, CancellationToken.None);
            Assert(repairs == 0 && terminal == SessionState.RolledBack && engine.Starts == 1 && !engine.IsRunning);
        }),
        ("Repair uses two fresh observations instead of counting one failed retry twice", async () =>
        {
            using var engine = new TestEngine(); using var cancel = new CancellationTokenSource();
            var n = 0; var running = 0;
            var controller = new SessionController(new HttpProbes(() => true, () => ++n is 1 or 4 or 5 or 7), engine)
            {
                InitialDelay = TimeSpan.Zero, IntervalOverride = TimeSpan.Zero,
                RepairAsync = (c, _) => Task.FromResult<AppConfig?>(c)
            };
            controller.StateChanged += (state, _) => { if (state == SessionState.Running && engine.Starts == 2) { running++; cancel.Cancel(); } };
            await controller.RunAsync(Config, cancel.Token);
            Assert(n == 7 && running == 1 && !engine.IsRunning);
        })
    ];
    sealed class TestProbes(Func<AppConfig, StrategySnapshot> get) : IStrategyProbe
    { public Task<StrategySnapshot> CheckAsync(AppConfig config, CancellationToken token) => Task.FromResult(get(config)); }
    sealed class TestEngine : IEngine
    {
        public bool IsRunning { get; private set; } public int Starts; public int Stops;
        public List<AppConfig> Configs = [];
        public void Start(AppConfig c) { Assert(!IsRunning); Configs.Add(c); Starts++; IsRunning = true; }
        public void Stop() { if (IsRunning) Stops++; IsRunning = false; }
        public void Dispose() => Stop();
    }
    sealed class HttpProbes(Func<bool> control, Func<bool>? target = null) : IProbeService
    {
        public Task<IReadOnlyList<ProbeResult>> CheckAsync(AppConfig c, CancellationToken ct)
        {
            var ok = control(); var targetOk = target?.Invoke() ?? true;
            return Task.FromResult<IReadOnlyList<ProbeResult>>([
                new("https://example.com/", true, ok, ok ? 200 : null, 1, "fixture", DateTimeOffset.Now),
                new("https://discord.com/", false, targetOk, targetOk ? 200 : null, 1, "fixture", DateTimeOffset.Now)]);
        }
    }
}
