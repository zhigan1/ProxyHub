using ProxyHub;
using ProxyHub.Adapters;

// ─────────────────────────────────────────────────────────────────────────────
// ProxyHub —— 进程入口
// 架构：Program（组装）→ AppFactory（路由）→ FailoverExecutor（熔断+故障转移）
//       → Registry（模型路由）→ AccountRegistry（账号池）→ Adapters（accounts/auth/models/stream）
// 热更新链：config.json 变更 → ConfigReloader → RuntimeConfig.Apply → 分组/熔断/账号即时重建
// ─────────────────────────────────────────────────────────────────────────────

var configPath = ProxyHubConfig.ResolvePath();
var config = ProxyHubConfig.Load(configPath);
var runtimeCfg = new RuntimeConfig(config);

var registry = new Registry();

// Token 用量持久化：SQLite（SqlSugar）。路径可配置（config.json usageDbPath / PROXY_HUB_USAGE_DB），
// 默认用户应用数据目录（不落仓库）；初始化失败降级为纯内存聚合，不影响代理主链路。
TokenUsageStore? usageStore = null;
try
{
    var dbPath = !string.IsNullOrWhiteSpace(config.UsageDbPath) ? config.UsageDbPath! : ProxyHubConfig.DefaultUsageDbPath();
    usageStore = new TokenUsageStore(dbPath);
    usageStore.EnsureCreated();
    Console.WriteLine($"Usage DB:            {dbPath}");
}
catch (Exception e)
{
    Console.WriteLine($"Usage persistence disabled: {e.Message}");
}
var usage = new UsageTracker(usageStore);
var groups = new ModelGroups(config.Groups);
var breakers = new CircuitBreakerRegistry(config.CircuitBreaker);
var accountRegistry = new AccountRegistry();
accountRegistry.SetManual(config.Accounts);
accountRegistry.ApplySettings(config.AccountSettings);

// 注册启用的适配器（OCP：新增平台只需加一个适配器类并在此注册）
var adapters = new List<IAdapter>();
if (config.IsAdapterEnabled("codebuddy")) adapters.Add(new CodeBuddyAdapter(TimeSpan.FromMilliseconds(config.TimeoutMs)));
if (config.IsAdapterEnabled("traecn")) adapters.Add(new TraeCnAdapter(TimeSpan.FromMilliseconds(config.TimeoutMs)));
if (config.IsAdapterEnabled("traework")) adapters.Add(new TraeWorkAdapter(TimeSpan.FromMilliseconds(config.TimeoutMs)));
if (config.IsAdapterEnabled("qoder")) adapters.Add(new QoderAdapter());
foreach (var adapter in adapters) registry.Register(adapter);

var store = new ConfigStore(runtimeCfg, configPath);
runtimeCfg.Changed += cfg =>
{
    // 热更新：分组 / 熔断参数 / 手工账号即时生效；端口与适配器开关为结构性配置，需重启
    groups.Update(cfg.Groups);
    breakers.UpdateSettings(cfg.CircuitBreaker);
    accountRegistry.SetManual(cfg.Accounts);
    accountRegistry.ApplySettings(cfg.AccountSettings);
};
using var reloader = new ConfigReloader(store);

var rt = new ProxyHubRuntime
{
    Config = runtimeCfg,
    Registry = registry,
    Usage = usage,
    Adapters = adapters,
    Accounts = accountRegistry,
    Breakers = breakers,
    Groups = groups,
    Executor = new FailoverExecutor(breakers),
    ConfigStore = store,
};

var app = AppFactory.Build(rt, args: args);

// 启动期后台刷新：账号扫描 + 动态模型列表（失败自动回退静态基线/无账号，不阻塞启动）
_ = Task.Run(async () =>
{
    try
    {
        await accountRegistry.RefreshAsync(adapters);
        Console.WriteLine($"Accounts discovered: {accountRegistry.TotalCount} (manual: {accountRegistry.ManualCount})");
    }
    catch (Exception e)
    {
        Console.WriteLine($"Account discovery skipped: {e.Message}");
    }
    try
    {
        await registry.RefreshAsync();
        Console.WriteLine(string.Join(Environment.NewLine,
            adapters.Select(a => $"  {a.Id.PadRight(10)} models: {registry.EffectiveCount(a.Id)}")));
        Console.WriteLine("Dynamic model refresh done (fallback to static on failure).");
    }
    catch (Exception e)
    {
        Console.WriteLine($"Dynamic model refresh skipped: {e.Message}");
    }

    // 后台自动签到：账号发现完成后，若启用则自动执行全账号签到，并按间隔周期巡检
    _ = Task.Run(async () =>
    {
        await Task.Delay(2000);
        if (!rt.Config.Current.Admin.AutoSignin) return;

        async Task TryAutoSigninAsync(string trigger)
        {
            try
            {
                Console.WriteLine($"[AutoSignin] Triggered ({trigger}), executing for all accounts...");
                var results = await AdminApi.SigninServiceInstance.RunAllAsync(rt);
                var succ = results.Count(r => r.TodayCheckedIn == true || r.Result is "CLAIMED" or "ALREADY" or "OK");
                Console.WriteLine($"[AutoSignin] Done: {succ}/{results.Count} accounts ready/signed in.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoSignin] Skipped/Failed: {ex.Message}");
            }
        }

        await TryAutoSigninAsync("startup");

        while (true)
        {
            var hours = Math.Max(1, rt.Config.Current.Admin.AutoSigninIntervalHours);
            await Task.Delay(TimeSpan.FromHours(hours));
            if (rt.Config.Current.Admin.AutoSignin)
                await TryAutoSigninAsync("periodic");
        }
    });
});

await app.StartAsync();
var listenUrls = app.Urls.Count > 0 ? string.Join(", ", app.Urls) : $"http://127.0.0.1:{config.Port}";
Console.WriteLine($"ProxyHub (.NET) listening on {listenUrls}");
var firstUrl = app.Urls.FirstOrDefault()?.TrimEnd('/') ?? $"http://127.0.0.1:{config.Port}";
var adminUrl = firstUrl.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1") + "/admin";
Console.WriteLine($"Admin UI:               {adminUrl}");
Console.WriteLine($"Config (hot reload):    {configPath}");
await app.WaitForShutdownAsync();

// 退出前清空用量持久化队列，避免尾部数据丢失
await usage.FlushAsync();
