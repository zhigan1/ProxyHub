using System.Text.Json.Nodes;

namespace ProxyHub.Adapters;

/// <summary>
/// CodeBuddy / WorkBuddy 适配器。
/// 凭据：自动读取 %LOCALAPPDATA%\CodeBuddyExtension\Data\Public\auth\*.info（桌面端自动刷新 token，重读即刷新）。
/// 上游：copilot.tencent.com/v2，标准 OpenAI SSE；请求头模拟 CodeBuddy CLI 客户端身份。
/// </summary>
public sealed class CodeBuddyAdapter : IAdapter
{
    private const string UpstreamBase = "https://copilot.tencent.com/v2";

    private static readonly string[] AuthFiles = BuildAuthFiles();

    // 固定请求头：模拟 CLI 客户端身份，让上游认为是合法 CodeBuddy CLI 请求
    private static readonly Dictionary<string, string> FixedHeaders = new()
    {
        ["X-Domain"] = "www.codebuddy.cn",
        ["X-Product"] = "SaaS",
        ["X-IDE-Type"] = "CLI",
        ["X-IDE-Name"] = "CLI",
        ["X-IDE-Version"] = "2.136.0",
        ["User-Agent"] = "CLI/2.136.0 CodeBuddy/2.136.0",
        ["X-Requested-With"] = "XMLHttpRequest",
        ["x-codebuddy-request"] = "1",
        ["X-Agent-Intent"] = "craft",
        ["X-Agent-Purpose"] = "conversation",
        ["X-Private-Data"] = "false",
    };

    private static readonly string[] Models =
    {
        "deepseek-flash", "deepseek-v4-pro", "deepseek-v4-flash", "minimax-m3", "minimax-m2.7",
        "glm-5.2", "glm-5.1", "glm-5v-turbo", "kimi-k3-1", "kimi-k2.7",
        "kimi-k2.6", "hy3",
    };

    private readonly TimeSpan _timeout;

    public CodeBuddyAdapter(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMilliseconds(120_000);
    }

    public string Id => "codebuddy";

    private static string[] BuildAuthFiles()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        return new[]
        {
            Path.Combine(local, "CodeBuddyExtension", "Data", "Public", "auth", "workbuddy-desktop.info"),
            Path.Combine(local, "CodeBuddyExtension", "Data", "Public", "auth", "Tencent-Cloud.coding-copilot.info"),
        };
    }

    /// <summary>遍历凭据文件，返回 { token, uid }；全部缺失则抛错（Fail Fast）。</summary>
    public Task<AuthInfo> GetAuthAsync(CancellationToken ct = default)
    {
        foreach (var file in AuthFiles)
        {
            try
            {
                var data = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                var token = data?["auth"]?["accessToken"]?.GetValue<string>();
                var uid = data?["account"]?["uid"]?.ToString();
                if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(uid))
                    return Task.FromResult(new AuthInfo(Token: token, Uid: uid));
            }
            catch
            {
                // 文件不存在或解析失败，尝试下一个
            }
        }
        throw new InvalidOperationException("未找到 CodeBuddy/WorkBuddy 登录凭据，请先登录 WorkBuddy 桌面端");
    }

    // WorkBuddy 桌面端会自动刷新 token 文件，重新读取即可拿到最新 token
    public Task<AuthInfo> RefreshAuthAsync(CancellationToken ct = default) => GetAuthAsync(ct);

    public IReadOnlyList<ModelRegistration> RegisterModels() =>
        Models.Select(m => new ModelRegistration($"codebuddy-{m}", m)).ToArray();

    public async Task ChatAsync(JsonObject request, Func<JsonObject, ValueTask> emit, CancellationToken ct = default)
    {
        var auth = await GetAuthAsync(ct);
        var headers = new Dictionary<string, string>(FixedHeaders)
        {
            ["Authorization"] = $"Bearer {auth.Token}",
            ["X-User-Id"] = auth.Uid!,
            ["Accept"] = "text/event-stream",
        };

        using var res = await UpstreamHttp.PostJsonAsync($"{UpstreamBase}/chat/completions", headers, request, _timeout, ct);
        if ((int)res.StatusCode >= 400)
        {
            var errBody = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"CodeBuddy upstream {(int)res.StatusCode}: {errBody[..Math.Min(200, errBody.Length)]}");
        }

        // 上游为标准 OpenAI SSE：按 data: 行增量解析并原样转发 chunk（含上游 usage 等字段）
        await foreach (var (_, data) in res.ReadSseAsync(ct))
        {
            if (data == "[DONE]") continue;
            if (JsonNode.Parse(data) is JsonObject chunk)
                await emit(chunk);
        }
    }
}
