using ProxyHub;
using ProxyHub.Adapters;

// ─────────────────────────────────────────────────────────────────────────────
// proxy-hub (.NET 10 复写版) —— 进程入口，对应上游 index.js 的 main()
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
Console.WriteLine($"proxy-hub (.NET) listening on http://127.0.0.1:{config.Port}");
app.Run();
