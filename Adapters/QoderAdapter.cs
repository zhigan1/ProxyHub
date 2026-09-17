using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ProxyHub.Adapters;

/// <summary>
/// Qoder（CN）适配器：不走 HTTP API，而是桥接本机 qoderclicn CLI 子进程
/// （--print --output-format stream-json），把 stream-json 行解析为 OpenAI chunk。
/// 账号：qoderclicn login 落盘（~/.qoderworkcn/.auth-cn/user）或环境变量 QODERCN_PERSONAL_ACCESS_TOKEN；
/// 手工账号可额外录入多把 PAT。
/// </summary>
public sealed class QoderAdapter : IAdapter
{
    private static readonly string CnHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".qoderworkcn");
    private static readonly string OAuthUserFile = Path.Combine(CnHome, ".auth-cn", "user");

    private static readonly (string ExternalId, string CliModel)[] ModelMap =
    {
        ("qoder-qwen3.7-max", "Qwen3.7-Max"),
        ("qoder-qwen3.6-plus", "Qwen3.6-Plus"),
        ("qoder-glm-5.2", "GLM-5.2"),
        ("qoder-glm-5.1", "GLM-5.1"),
        ("qoder-kimi-k2.6", "Kimi-K2.6"),
        ("qoder-deepseek-v4-pro", "DeepSeek-V4-Pro"),
        ("qoder-deepseek-v4-flash", "DeepSeek-V4-Flash"),
    };

    private readonly string _command;
    private readonly string? _pat;

    public QoderAdapter(string? command = null, string? pat = null)
    {
        _command = command ?? Environment.GetEnvironmentVariable("QODERCN_CLI") ?? "qoderclicn";
        _pat = pat ?? Environment.GetEnvironmentVariable("QODERCN_PERSONAL_ACCESS_TOKEN");
    }

    public string Id => "qoder";

    /// <summary>枚举账号：CLI 落盘 OAuth + PAT 环境变量（手工 PAT 账号由 AccountRegistry 合并）。</summary>
    public Task<IReadOnlyList<AdapterAccount>> DiscoverAccountsAsync(CancellationToken ct = default)
    {
        var accounts = new List<AdapterAccount>();
        if (File.Exists(OAuthUserFile))
            accounts.Add(new AdapterAccount(Id, "oauth", "CLI 登录", SourceFile: OAuthUserFile));
        if (!string.IsNullOrEmpty(_pat))
            accounts.Add(new AdapterAccount(Id, "pat", "PAT 环境变量", Pat: _pat));
        return Task.FromResult<IReadOnlyList<AdapterAccount>>(accounts);
    }

    /// <summary>指定账号优先（PAT 账号直接返回 PAT）；account 为 null 时回落默认（落盘 OAuth → PAT）。</summary>
    public Task<AuthInfo> GetAuthAsync(AdapterAccount? account = null, CancellationToken ct = default)
    {
        if (account?.Pat is not null)
            return Task.FromResult(new AuthInfo(Pat: account.Pat));
        if (account?.SourceFile is not null && File.Exists(account.SourceFile))
            return Task.FromResult(new AuthInfo(OAuth: true));
        if (File.Exists(OAuthUserFile))
            return Task.FromResult(new AuthInfo(OAuth: true));
        if (!string.IsNullOrEmpty(_pat))
            return Task.FromResult(new AuthInfo(Pat: _pat));
        throw new InvalidOperationException("Qoder: 既无 qoderclicn login 落盘凭据，也无 QODERCN_PERSONAL_ACCESS_TOKEN");
    }

    public Task<AuthInfo> RefreshAuthAsync(AdapterAccount? account = null, CancellationToken ct = default) =>
        GetAuthAsync(account, ct);

    public IReadOnlyList<ModelRegistration> RegisterModels() =>
        ModelMap.Select(m => new ModelRegistration(m.ExternalId, m.CliModel)).ToArray();

    /// <summary>
    /// 动态模型源：调用 qoderclicn --list-models 输出当前账号可用模型。
    /// 解析采用宽容策略（JSON 段或逐行取模型 token），无法解析或 CLI 缺失则抛错→Registry 回退静态基线。
    /// </summary>
    public async Task<IReadOnlyList<ModelRegistration>?> FetchModelsAsync(CancellationToken ct = default)
    {
        var launcher = new ProcessStartInfo(_command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        launcher.ArgumentList.Add("--list-models");
        if (!string.IsNullOrEmpty(_pat) && !File.Exists(OAuthUserFile))
            launcher.Environment["QODERCN_PERSONAL_ACCESS_TOKEN"] = _pat;

        using var child = Process.Start(launcher)
            ?? throw new InvalidOperationException($"无法启动 {_command} 查询模型");
        var output = await child.StandardOutput.ReadToEndAsync(ct);
        var err = await child.StandardError.ReadToEndAsync(ct);
        await child.WaitForExitAsync(ct);
        if (child.ExitCode != 0)
            throw new InvalidOperationException($"qoderclicn --list-models exited {child.ExitCode}: {err[..Math.Min(200, err.Length)]}");

        var names = ParseListModelsOutput(output);
        return names.Count == 0
            ? null
            : names.Select(n => new ModelRegistration($"{Id}-{n}", n)).ToList();
    }

    /// <summary>
    /// 解析 --list-models 输出：优先整段 JSON 数组/带 model 字段的对象；否则逐行清洗（去表格线/表头/空行）取模型名。
    /// </summary>
    public static List<string> ParseListModelsOutput(string output)
    {
        var result = new List<string>();
        var bracket = output.IndexOf('[');
        if (bracket >= 0)
        {
            var end = output.LastIndexOf(']');
            if (end > bracket)
            {
                try
                {
                    if (JsonNode.Parse(output[bracket..(end + 1)]) is JsonArray arr)
                    {
                        foreach (var item in arr)
                        {
                            var name = item is JsonObject obj
                                ? obj["model"]?.GetValue<string>() ?? obj["id"]?.GetValue<string>()
                                : (item as JsonValue)?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(name))
                                result.Add(name.Trim());
                        }
                        return result;
                    }
                }
                catch { /* 非 JSON，走行解析 */ }
            }
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line is "Model" or "Name" || line.StartsWith("---", StringComparison.Ordinal)) continue;
            // 去 markdown 表格边线后取首个字段
            var first = line.Split('|').Select(t => t.Trim()).FirstOrDefault(t => t.Length > 0);
            if (first is null) continue;
            if (first.Contains('\t')) first = first.Split('\t')[0];
            if (first.Length > 0 && !first.Contains(' '))
                result.Add(first);
        }
        return result;
    }

    /// <summary>从 stream-json 单行解析出文本增量；非文本/无文本返回 null。</summary>
    public static string? ParseCliDelta(string line)
    {
        JsonObject? rec;
        try
        {
            rec = JsonNode.Parse(line) as JsonObject;
        }
        catch
        {
            return null;
        }
        if (rec?["type"]?.GetValue<string>() != "assistant") return null;
        if (rec["message"]?["content"] is not JsonArray content) return null;

        var text = string.Concat(content.OfType<JsonObject>()
            .Where(c => c["type"]?.GetValue<string>() == "text")
            .Select(c => c["text"]?.GetValue<string>()));
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public async Task ChatAsync(JsonObject request, AdapterAccount? account, Func<JsonObject, ValueTask> emit, CancellationToken ct = default)
    {
        var auth = await GetAuthAsync(account, ct);

        var model = request["model"]?.GetValue<string>() ?? "Qwen3.7-Max";
        var messages = request["messages"] as JsonArray ?? new JsonArray();
        var system = messages.OfType<JsonObject>()
            .FirstOrDefault(m => m["role"]?.GetValue<string>() == "system")?["content"]?.GetValue<string>();
        var userMsg = string.Join('\n', messages.OfType<JsonObject>()
            .Where(m => m["role"]?.GetValue<string>() != "system")
            .Select(m => m["content"]?.GetValue<string>()));

        var psi = new ProcessStartInfo(_command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--dangerously-skip-permissions");
        if (!string.IsNullOrEmpty(system))
        {
            psi.ArgumentList.Add("--append-system-prompt");
            psi.ArgumentList.Add(system);
        }
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(userMsg);
        // PAT 账号强制注入本账号令牌；无账号指定时回落到构造期 PAT（且落盘 OAuth 不存在）
        if (auth.Pat is not null)
            psi.Environment["QODERCN_PERSONAL_ACCESS_TOKEN"] = auth.Pat;
        else if (!string.IsNullOrEmpty(_pat) && !File.Exists(OAuthUserFile))
            psi.Environment["QODERCN_PERSONAL_ACCESS_TOKEN"] = _pat;

        using var child = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动 {_command}");
        child.StandardInput.Close();

        var stderrTask = child.StandardError.ReadToEndAsync(ct);
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 增量读取 stream-json 行，文本增量即时 emit（真流式）
        while (await child.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            var text = ParseCliDelta(line);
            if (text is null) continue;
            await emit(new JsonObject
            {
                ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
                ["object"] = "chat.completion.chunk",
                ["created"] = created,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject { ["content"] = text },
                    ["finish_reason"] = null,
                }),
            });
        }

        await child.WaitForExitAsync(ct);
        if (child.ExitCode != 0)
        {
            var stderr = await stderrTask;
            throw new UpstreamException(502,
                $"qoderclicn exited {child.ExitCode}: {stderr[..Math.Min(300, stderr.Length)]}");
        }

        await emit(new JsonObject
        {
            ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = new JsonObject(),
                ["finish_reason"] = "stop",
            }),
        });
    }
}
