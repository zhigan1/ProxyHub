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
var usage = new UsageTracker();
var groups = new ModelGroups(config.Groups);
var breakers = new CircuitBreakerRegistry(config.CircuitBreaker);
var accountRegistry = new AccountRegistry();
accountRegistry.SetManual(config.Accounts);

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

var app = AppFactory.Build(rt);
app.Urls.Clear();
app.Urls.Add($"http://127.0.0.1:{config.Port}");

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
});

await app.StartAsync();
Console.WriteLine($"ProxyHub (.NET) listening on http://127.0.0.1:{config.Port}");
Console.WriteLine($"Admin UI:               http://127.0.0.1:{config.Port}/admin");
Console.WriteLine($"Config (hot reload):    {configPath}");
await app.WaitForShutdownAsync();
