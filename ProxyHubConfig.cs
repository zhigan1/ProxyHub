using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>
/// 网关配置。优先级：环境变量 &gt; config.json &gt; 默认值。
/// （注意：上游 config.js 注释写"文件优先级最高"，但实际代码与 README 均为环境变量优先，此处按实际行为实现。）
/// </summary>
public sealed record ProxyHubConfig
{
    public int Port { get; init; } = 8787;
    public string ProxyKey { get; init; } = "";
    public int TimeoutMs { get; init; } = 120_000;
    public IReadOnlyDictionary<string, bool> Adapters { get; init; } = new Dictionary<string, bool>
    {
        ["codebuddy"] = true,
        ["traecn"] = true,
        ["traework"] = true,
        ["qoder"] = true,
    };

    public bool IsAdapterEnabled(string id) => Adapters.TryGetValue(id, out var on) && on;

    public static ProxyHubConfig Load(string? configPath = null)
    {
        var file = ReadConfigFile(configPath);
        string? Env(string name) => Environment.GetEnvironmentVariable(name);

        return new ProxyHubConfig
        {
            Port = ParseInt(Env("PROXY_HUB_PORT") ?? FileStr(file, "port"), 8787),
            ProxyKey = Env("PROXY_HUB_KEY") ?? FileStr(file, "proxyKey") ?? "",
            TimeoutMs = ParseInt(Env("PROXY_HUB_TIMEOUT") ?? FileStr(file, "timeoutMs"), 120_000),
            Adapters = new Dictionary<string, bool>
            {
                ["codebuddy"] = ParseBool(Env("PROXY_ADAPTER_CODEBUDDY"), FileBool(file, "codebuddy") ?? true),
                ["traecn"] = ParseBool(Env("PROXY_ADAPTER_TRAECN"), FileBool(file, "traecn") ?? true),
                ["traework"] = ParseBool(Env("PROXY_ADAPTER_TRAEWORK"), FileBool(file, "traework") ?? true),
                ["qoder"] = ParseBool(Env("PROXY_ADAPTER_QODER"), FileBool(file, "qoder") ?? true),
            },
        };
    }

    private static JsonObject? ReadConfigFile(string? explicitPath)
    {
        var path = explicitPath
            ?? Environment.GetEnvironmentVariable("PROXY_HUB_CONFIG")
            ?? Path.Combine(AppContext.BaseDirectory, "config.json");
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

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, out var n) ? n : fallback;

    /// <summary>与上游 bool() 一致：未定义时用默认值；字符串仅 "false"（大小写不敏感）为假。</summary>
    private static bool ParseBool(string? raw, bool fallback) =>
        raw is null ? fallback : !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
}
