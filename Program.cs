using ProxyHub;
using ProxyHub.Adapters;

// ─────────────────────────────────────────────────────────────────────────────
// ProxyHub —— 进程入口
// 架构：Program（组装）→ AppFactory（路由）→ Registry（模型路由）→ Adapters（auth/models/request/stream）
// ─────────────────────────────────────────────────────────────────────────────

var config = ProxyHubConfig.Load();
var registry = new Registry();
var usage = new UsageTracker();

// 注册启用的适配器（OCP：新增平台只需加一个适配器类并在此注册）
var adapters = new List<IAdapter>();
if (config.IsAdapterEnabled("codebuddy")) adapters.Add(new CodeBuddyAdapter(TimeSpan.FromMilliseconds(config.TimeoutMs)));
if (config.IsAdapterEnabled("traecn")) adapters.Add(new TraeCnAdapter(TimeSpan.FromMilliseconds(config.TimeoutMs)));
if (config.IsAdapterEnabled("traework")) adapters.Add(new TraeWorkAdapter(TimeSpan.FromMilliseconds(config.TimeoutMs)));
if (config.IsAdapterEnabled("qoder")) adapters.Add(new QoderAdapter());
foreach (var adapter in adapters) registry.Register(adapter);

var app = AppFactory.Build(config, registry, usage, adapters);
app.Urls.Clear();
app.Urls.Add($"http://127.0.0.1:{config.Port}");

// 启动期后台刷新动态模型列表（失败自动回退静态基线，不阻塞启动）
_ = Task.Run(async () =>
{
    try
    {
        await registry.RefreshAsync();
        Console.WriteLine(String.Join(Environment.NewLine,
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
await app.WaitForShutdownAsync();
