using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>虚拟分组定义：match 为通配模式列表（'*' 任意串，大小写不敏感），prefer 为平台优先级。</summary>
public sealed record GroupConfig
{
    public IReadOnlyList<string> Match { get; init; } = new[] { "*" };
    public IReadOnlyList<string> Prefer { get; init; } = Array.Empty<string>();
    public string Description { get; init; } = "";
}

/// <summary>熔断参数：连续失败阈值 / Open 冷却秒数 / 是否启用半开探测。</summary>
public sealed record CircuitBreakerConfig
{
    public int FailureThreshold { get; init; } = 2;
    public int CooldownSeconds { get; init; } = 60;
    public bool HalfOpenProbe { get; init; } = true;
}

/// <summary>手工账号：字段按适配器解释（CodeBuddy=authFile，Trae 系=storageFile，Qoder=pat）。</summary>
public sealed record ManualAccount
{
    public string Label { get; init; } = "";
    public string? AuthFile { get; init; }
    public string? StorageFile { get; init; }
    public string? Pat { get; init; }
}

/// <summary>账号状态持久化：禁用、删除、自定义顺位、自定义/缓存积分。</summary>
public sealed record AccountSetting
{
    public bool Disabled { get; init; }
    public bool Deleted { get; init; }
    public int? Order { get; init; }
    public int? Credits { get; init; }
}

/// <summary>管理页与自动签到配置。</summary>
public sealed record AdminConfig
{
    public bool Enabled { get; init; } = true;
    public bool FailoverHeader { get; init; } = true;
    public bool AutoSignin { get; init; } = true;
    public int AutoSigninIntervalHours { get; init; } = 12;
}

/// <summary>
/// 网关配置 v2。优先级：环境变量 &gt; config.json &gt; 默认值。
/// 分组 / 熔断 / 账号 / 管理段均支持热更新；端口 / 适配器开关为结构性配置，仅启动生效。
/// </summary>
public sealed record ProxyHubConfig
{
    public int Port { get; init; } = 8265;
    public string ProxyKey { get; init; } = "";
    public int TimeoutMs { get; init; } = 120_000;

    /// <summary>Token 用量 SQLite 库文件路径；空 = 默认应用数据目录（不落仓库目录）。</summary>
    public string? UsageDbPath { get; init; }
    public IReadOnlyDictionary<string, bool> Adapters { get; init; } = new Dictionary<string, bool>
    {
        ["codebuddy"] = true,
        ["traecn"] = true,
        ["traework"] = true,
        ["qoder"] = true,
    };

    public IReadOnlyDictionary<string, GroupConfig> Groups { get; init; } = DefaultGroups();
    public CircuitBreakerConfig CircuitBreaker { get; init; } = new();
    public IReadOnlyDictionary<string, IReadOnlyList<ManualAccount>> Accounts { get; init; }
        = new Dictionary<string, IReadOnlyList<ManualAccount>>();
    public IReadOnlyDictionary<string, AccountSetting> AccountSettings { get; init; }
        = new Dictionary<string, AccountSetting>(StringComparer.OrdinalIgnoreCase);
    public AdminConfig Admin { get; init; } = new();

    /// <summary>实际使用的配置文件路径（不存在时也为 admin 写回指定落点）。</summary>
    public string? SourcePath { get; init; }

    public bool IsAdapterEnabled(string id) => Adapters.TryGetValue(id, out var on) && on;

    /// <summary>用量库默认路径：用户应用数据目录/ProxyHub/usage.db（Windows=%LOCALAPPDATA%，Linux=~/.local/share）。</summary>
    public static string DefaultUsageDbPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyHub", "usage.db");

    /// <summary>配置文件路径解析：显式参数 &gt; PROXY_HUB_CONFIG 环境变量 &gt; 程序目录 config.json。</summary>
    public static string ResolvePath(string? explicitPath = null) =>
        explicitPath
        ?? Environment.GetEnvironmentVariable("PROXY_HUB_CONFIG")
        ?? Path.Combine(AppContext.BaseDirectory, "config.json");

    public static IReadOnlyDictionary<string, GroupConfig> DefaultGroups() =>
        new Dictionary<string, GroupConfig>(StringComparer.Ordinal)
        {
            ["auto-flash"] = new()
            {
                Match = new[] { "*-flash" },
                Prefer = new[] { "codebuddy", "traecn", "traework", "qoder" },
                Description = "速度优先：全部 flash 档模型自动切换",
            },
            ["auto-pro"] = new()
            {
                Match = new[] { "*-pro", "*-max" },
                Prefer = new[] { "codebuddy", "traecn", "traework", "qoder" },
                Description = "质量优先：pro / max 档模型自动切换",
            },
            ["auto-glm"] = new()
            {
                Match = new[] { "*glm*" },
                Prefer = new[] { "codebuddy", "traecn", "traework", "qoder" },
                Description = "GLM 家族全部版本",
            },
            ["auto"] = new()
            {
                Match = new[] { "*" },
                Prefer = new[] { "codebuddy", "traecn", "traework", "qoder" },
                Description = "全部模型兜底",
            },
        };

    public static ProxyHubConfig Load(string? configPath = null)
    {
        var path = ResolvePath(configPath);
        var file = ReadConfigFile(path);
        string? Env(string name) => Environment.GetEnvironmentVariable(name);

        return new ProxyHubConfig
        {
            SourcePath = path,
            Port = ParseInt(Env("PROXY_HUB_PORT") ?? FileStr(file, "port"), 8265),
            ProxyKey = Env("PROXY_HUB_KEY") ?? FileStr(file, "proxyKey") ?? "",
            TimeoutMs = ParseInt(Env("PROXY_HUB_TIMEOUT") ?? FileStr(file, "timeoutMs"), 120_000),
            UsageDbPath = Env("PROXY_HUB_USAGE_DB") ?? FileStr(file, "usageDbPath"),
            Adapters = new Dictionary<string, bool>
            {
                ["codebuddy"] = ParseBool(Env("PROXY_ADAPTER_CODEBUDDY"), FileBool(file, "codebuddy") ?? true),
                ["traecn"] = ParseBool(Env("PROXY_ADAPTER_TRAECN"), FileBool(file, "traecn") ?? true),
                ["traework"] = ParseBool(Env("PROXY_ADAPTER_TRAEWORK"), FileBool(file, "traework") ?? true),
                ["qoder"] = ParseBool(Env("PROXY_ADAPTER_QODER"), FileBool(file, "qoder") ?? true),
            },
            Groups = FileGroups(file) ?? DefaultGroups(),
            CircuitBreaker = FileCircuitBreaker(file) ?? new CircuitBreakerConfig(),
            Accounts = FileAccounts(file),
            AccountSettings = FileAccountSettings(file),
            Admin = FileAdmin(file) ?? new AdminConfig(),
        };
    }

    private static IReadOnlyDictionary<string, AccountSetting> FileAccountSettings(JsonObject? file)
    {
        var result = new Dictionary<string, AccountSetting>(StringComparer.OrdinalIgnoreCase);
        if (file?["accountSettings"] is not JsonObject settings) return result;
        foreach (var (key, node) in settings)
        {
            if (node is not JsonObject s) continue;
            result[key] = new AccountSetting
            {
                Disabled = s["disabled"] is JsonValue dv && dv.TryGetValue<bool>(out var d) && d,
                Deleted = s["deleted"] is JsonValue delv && delv.TryGetValue<bool>(out var del) && del,
                Order = s["order"] is JsonValue ov && ov.TryGetValue<int>(out var o) ? o : null,
                Credits = s["credits"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : null,
            };
        }
        return result;
    }

    private static JsonObject? ReadConfigFile(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string? FileStr(JsonObject? file, string key) =>
        file?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : file?[key]?.ToJsonString();

    private static bool? FileBool(JsonObject? file, string key)
    {
        if (file?["adapters"] is not JsonObject adapters) return null;
        if (adapters[key] is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s)) return ParseBool(s, true);
        return null;
    }

    private static IReadOnlyDictionary<string, GroupConfig>? FileGroups(JsonObject? file)
    {
        if (file?["groups"] is not JsonObject groups || groups.Count == 0) return null;
        var result = new Dictionary<string, GroupConfig>(StringComparer.Ordinal);
        foreach (var (name, node) in groups)
        {
            if (node is not JsonObject g) continue;
            result[name] = new GroupConfig
            {
                Match = StrArray(g["match"]) ?? new[] { "*" },
                Prefer = StrArray(g["prefer"]) ?? Array.Empty<string>(),
                Description = g["description"]?.GetValue<string>() ?? "",
            };
        }
        return result.Count > 0 ? result : null;
    }

    private static CircuitBreakerConfig? FileCircuitBreaker(JsonObject? file)
    {
        if (file?["circuitBreaker"] is not JsonObject cb) return null;
        return new CircuitBreakerConfig
        {
            FailureThreshold = ParseInt(cb["failureThreshold"]?.ToString(), 2),
            CooldownSeconds = ParseInt(cb["cooldownSeconds"]?.ToString(), 60),
            HalfOpenProbe = cb["halfOpenProbe"] is JsonValue hv && hv.TryGetValue<bool>(out var b) ? b : true,
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ManualAccount>> FileAccounts(JsonObject? file)
    {
        var result = new Dictionary<string, IReadOnlyList<ManualAccount>>(StringComparer.Ordinal);
        if (file?["accounts"] is not JsonObject accounts) return result;
        foreach (var (adapterId, node) in accounts)
        {
            if (node is not JsonArray arr) continue;
            var list = new List<ManualAccount>();
            foreach (var item in arr.OfType<JsonObject>())
            {
                list.Add(new ManualAccount
                {
                    Label = item["label"]?.GetValue<string>() ?? "",
                    AuthFile = item["authFile"]?.GetValue<string>(),
                    StorageFile = item["storageFile"]?.GetValue<string>(),
                    Pat = item["pat"]?.GetValue<string>(),
                });
            }
            result[adapterId] = list;
        }
        return result;
    }

    private static AdminConfig? FileAdmin(JsonObject? file)
    {
        if (file?["admin"] is not JsonObject ad) return null;
        return new AdminConfig
        {
            Enabled = ad["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var e) ? e : true,
            FailoverHeader = ad["failoverHeader"] is JsonValue fv && fv.TryGetValue<bool>(out var f) ? f : true,
            AutoSignin = ad["autoSignin"] is JsonValue av && av.TryGetValue<bool>(out var a) ? a : true,
            AutoSigninIntervalHours = ad["autoSigninIntervalHours"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : 12,
        };
    }

    private static IReadOnlyList<string>? StrArray(JsonNode? node)
    {
        if (node is not JsonArray arr) return null;
        var list = arr.Where(v => v is JsonValue).Select(v => v!.GetValue<string>()).Where(s => s.Length > 0).ToList();
        return list.Count > 0 ? list : null;
    }

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, out var n) ? n : fallback;

    /// <summary>与上游 bool() 一致：未定义时用默认值；字符串仅 "false"（大小写不敏感）为假。</summary>
    private static bool ParseBool(string? raw, bool fallback) =>
        raw is null ? fallback : !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
}
