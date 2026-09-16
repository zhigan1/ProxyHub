using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ProxyHub.Adapters;

/// <summary>
/// Qoder（CN）适配器：不走 HTTP API，而是桥接本机 qoderclicn CLI 子进程
/// （--print --output-format stream-json），把 stream-json 行解析为 OpenAI chunk。
/// 凭据二选一：qoderclicn login 落盘（~/.qoderworkcn/.auth-cn/user）或环境变量 QODERCN_PERSONAL_ACCESS_TOKEN。
/// 对应上游 adapters/qoder.js。
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

    public Task<AuthInfo> GetAuthAsync(CancellationToken ct = default)
    {
        if (File.Exists(OAuthUserFile)) return Task.FromResult(new AuthInfo(OAuth: true));
        if (!string.IsNullOrEmpty(_pat)) return Task.FromResult(new AuthInfo(Pat: _pat));
        throw new InvalidOperationException("Qoder: 既无 qoderclicn login 落盘凭据，也无 QODERCN_PERSONAL_ACCESS_TOKEN");
    }

    public Task<AuthInfo> RefreshAuthAsync(CancellationToken ct = default) => GetAuthAsync(ct);

    public IReadOnlyList<ModelRegistration> RegisterModels() =>
        ModelMap.Select(m => new ModelRegistration(m.ExternalId, m.CliModel)).ToArray();

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

    public async Task ChatAsync(JsonObject request, Func<JsonObject, ValueTask> emit, CancellationToken ct = default)
    {
        await GetAuthAsync(ct);

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
        // 有落盘 OAuth 凭据则以其为准；否则注入 PAT
        if (!string.IsNullOrEmpty(_pat) && !File.Exists(OAuthUserFile))
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
            throw new InvalidOperationException($"qoderclicn exited {child.ExitCode}: {stderr[..Math.Min(300, stderr.Length)]}");
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
