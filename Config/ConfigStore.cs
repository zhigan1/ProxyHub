using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>
/// 配置文件存取：config.json 的单一事实来源。读=Load（环境变量仍优先），写=原子替换+立即热更新。
/// 管理页的一切"可配置修改"最终都落盘到本文件，由热更新链路广播到运行时各组件。
/// </summary>
public sealed class ConfigStore
{
    private readonly RuntimeConfig _runtime;

    public string Path { get; }

    public ConfigStore(RuntimeConfig runtime, string path)
    {
        _runtime = runtime;
        Path = path;
    }

    public ProxyHubConfig Reload()
    {
        var cfg = ProxyHubConfig.Load(Path);
        _runtime.Apply(cfg);
        return cfg;
    }

    /// <summary>读取原始 JSON；文件不存在时返回 null（调用方自行决定是否创建骨架）。</summary>
    public JsonObject? ReadRaw()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取原始 JSON；文件不存在时返回包含默认值的骨架。</summary>
    public JsonObject ReadOrCreate() =>
        ReadRaw() ?? new JsonObject
        {
            ["port"] = 8265,
            ["proxyKey"] = "",
            ["timeoutMs"] = 120_000,
            ["adapters"] = new JsonObject
            {
                ["codebuddy"] = true,
                ["traecn"] = true,
                ["traework"] = true,
                ["qoder"] = true,
            },
            ["groups"] = SerializeGroups(ProxyHubConfig.DefaultGroups()),
            ["circuitBreaker"] = new JsonObject
            {
                ["failureThreshold"] = 2,
                ["cooldownSeconds"] = 60,
                ["halfOpenProbe"] = true,
            },
            ["admin"] = new JsonObject { ["enabled"] = true, ["failoverHeader"] = true },
        };

    /// <summary>原子写入（临时文件 + Move 覆盖）并立即热更新。</summary>
    public void WriteRaw(JsonObject raw)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, raw.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, Path, overwrite: true);
        Reload();
    }

    internal static JsonObject SerializeGroups(IReadOnlyDictionary<string, GroupConfig> groups)
    {
        var obj = new JsonObject();
        foreach (var (name, g) in groups)
        {
            obj[name] = new JsonObject
            {
                ["match"] = new JsonArray(g.Match.Select(m => JsonValue.Create(m)).ToArray()),
                ["prefer"] = new JsonArray(g.Prefer.Select(p => JsonValue.Create(p)).ToArray()),
                ["description"] = g.Description,
            };
        }
        return obj;
    }
}

/// <summary>
/// 配置热更新监听：FileSystemWatcher + 300ms 防抖。
/// 监听到变更即 Reload（幂等，与管理页直写触发的 Reload 合并）；目录尚不存在时自动停用。
/// </summary>
public sealed class ConfigReloader : IDisposable
{
    private readonly ConfigStore _store;
    private readonly FileSystemWatcher? _watcher;
    private int _reloadQueued;

    public ConfigReloader(ConfigStore store)
    {
        _store = store;
        var fullPath = System.IO.Path.GetFullPath(store.Path);
        var dir = System.IO.Path.GetDirectoryName(fullPath);
        if (dir is null || !Directory.Exists(dir)) return;

        _watcher = new FileSystemWatcher(dir)
        {
            Filter = System.IO.Path.GetFileName(fullPath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();
    }

    private void ScheduleReload()
    {
        if (Interlocked.Exchange(ref _reloadQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300); // 防抖：编辑器保存常触发多次事件
                Interlocked.Exchange(ref _reloadQueued, 0);
                _store.Reload();
            }
            catch
            {
                // 重载失败保留旧配置，下次文件变更再试
            }
        });
    }

    public void Dispose() => _watcher?.Dispose();
}
