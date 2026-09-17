using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ProxyHub.Adapters;

/// <summary>
/// Trae 系适配器基类：Trae CN 与 TraeWork 共享同一套 tc 解密、上游主机、请求头伪装与 SSE 归一化，
/// 差异仅在凭据目录 / chat function / token 刷新策略（见各子类）。
/// 多账号：默认 storage.json + %APPDATA% 下同家族 profile 目录（ProfilePattern 通配）全扫描。
/// </summary>
public abstract class TraeAdapterBase : IAdapter
{
    protected const string UpstreamBase = "https://trae-api-cn.mchost.guru";
    protected const string XAppId = "6eefa01c-1036-4c7e-9ca5-d891f63bfcd8";
    protected const string IdeVersion = "3.3.67";
    protected const string IdeVersionCode = "20260401";
    protected const string AuthStorageKey = "iCubeAuthInfo://icube.cloudide";

    /// <summary>候选上游端点：依次尝试，全部失败才报错（端点会随版本漂移，多候选提高存活率）。</summary>
    protected static readonly string[] ChatEndpoints =
    {
        "/api/agent/v3/llm_utils_chat",
        "/api/ide/v1/chat",
        "/api/agent/v3/create_agent_task",
    };

    protected static readonly string[] TraeModels =
    {
        "glm-5.2", "glm-5.1", "glm-5", "qwen-3.7-plus", "kimi-k2.6", "deepseek-v4-pro", "deepseek-v4-flash",
    };

    protected readonly CredentialsCache Cache = new();
    protected readonly TimeSpan Timeout;

    protected TraeAdapterBase(TimeSpan? timeout = null)
    {
        Timeout = timeout ?? TimeSpan.FromMilliseconds(120_000);
    }

    public abstract string Id { get; }

    /// <summary>chat function 名：traecn=inline_chat，traework=solo_work_lite（轻排队）。</summary>
    protected abstract string ChatFunction { get; }

    /// <summary>本机默认 storage.json 的完整路径。</summary>
    protected abstract string StorageFile { get; }

    /// <summary>%APPDATA% 下同家族 profile 目录通配（如 'Trae CN*'），用于多账号扫描。</summary>
    protected abstract string ProfilePattern { get; }

    /// <summary>Trae 桌面版 storage.json 路径（productDir 形如 'Trae CN' / 'TRAE SOLO CN'）。</summary>
    public static string TraeStoragePath(string productDir) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            productDir, "User", "globalStorage", "storage.json");

    /// <summary>枚举账号：默认 storage.json + %APPDATA% 下匹配 ProfilePattern 的各 profile 目录。</summary>
    public Task<IReadOnlyList<AdapterAccount>> DiscoverAccountsAsync(CancellationToken ct = default)
    {
        var accounts = new List<AdapterAccount>();
        if (File.Exists(StorageFile))
            accounts.Add(new AdapterAccount(Id, "default", "默认目录", SourceFile: StorageFile));

        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = new DirectoryInfo(appData);
            if (dir.Exists)
            {
                foreach (var sub in dir.GetDirectories(ProfilePattern).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var storage = Path.Combine(sub.FullName, "User", "globalStorage", "storage.json");
                    if (!File.Exists(storage)) continue;
                    if (accounts.Any(a => string.Equals(a.SourceFile, storage, StringComparison.OrdinalIgnoreCase))) continue;
                    accounts.Add(new AdapterAccount(Id, sub.Name, sub.Name, SourceFile: storage));
                }
            }
        }
        catch
        {
            // 目录扫描失败仅影响多账号发现，不影响默认路径
        }
        return Task.FromResult<IReadOnlyList<AdapterAccount>>(accounts);
    }

    private static string AuthKey(AdapterAccount? account) => "auth:" + (account?.SourceFile ?? "default");

    /// <summary>读取指定账号凭据（tc 解密），返回 { token, refreshToken, userId }。</summary>
    protected AuthInfo ReadStorageAuth(string file)
    {
        var storage = JsonNode.Parse(File.ReadAllText(file)) as JsonObject
            ?? throw new InvalidDataException($"{file} 解析失败");
        var blob = storage[AuthStorageKey]?.GetValue<string>()
            ?? throw new InvalidOperationException($"{file} 缺少 {AuthStorageKey}，请先登录对应桌面端");

        // 明文分支（国际版 SG 直接存 JSON 字符串）；密文分支走 tc 解密
        var json = blob.TrimStart().StartsWith('{') ? blob : TcCrypto.DecryptTc(blob);
        var auth = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("iCubeAuthInfo 解密后不是合法 JSON");
        return new AuthInfo(
            Token: auth["token"]?.GetValue<string>(),
            RefreshToken: auth["refreshToken"]?.GetValue<string>(),
            UserId: auth["userId"]?.GetValue<string>() ?? auth["uid"]?.GetValue<string>());
    }

    public virtual Task<AuthInfo> GetAuthAsync(AdapterAccount? account = null, CancellationToken ct = default) =>
        Cache.GetAsync(AuthKey(account), () => Task.FromResult((
            ReadStorageAuth(account?.SourceFile ?? StorageFile),
            DateTimeOffset.UtcNow.AddMinutes(10))));

    public virtual Task<AuthInfo> RefreshAuthAsync(AdapterAccount? account = null, CancellationToken ct = default)
    {
        Cache.Invalidate(AuthKey(account));
        return GetAuthAsync(account, ct);
    }

    public IReadOnlyList<ModelRegistration> RegisterModels() =>
        TraeModels.Select(m => new ModelRegistration($"{Id}-{m}", m)).ToArray();

    /// <summary>state.vscdb 与 storage.json 同目录（User\globalStorage），多账号 profile 天然对齐。</summary>
    protected virtual string StateDbPath =>
        Path.Combine(Path.GetDirectoryName(StorageFile)!, "state.vscdb");

    /// <summary>
    /// 动态模型发现：读取本机 Trae 桌面端 state.vscdb 中服务端下发的模型缓存
    /// （键名动态匹配 *AI.agent.modeListMap 与 *AI.agent.model.model_list_map），
    /// 解析 JSON 提取聊天模型名，去重并稳定排序。
    /// 零上游调用、零凭据读取；缓存缺失/损坏/不可用时返回 null，由 Registry 回退静态基线。
    /// </summary>
    public Task<IReadOnlyList<ModelRegistration>?> FetchModelsAsync(CancellationToken ct = default) =>
        FetchModelsAsync(StateDbPath);

    /// <summary>同上，但显式指定 state.vscdb 路径（可测试注入）。</summary>
    public Task<IReadOnlyList<ModelRegistration>?> FetchModelsAsync(string stateDbPath, CancellationToken ct = default)
    {
        IReadOnlyList<string>? names = null;
        try
        {
            var entries = StateVscDb.ReadItemTable(stateDbPath, "AI.agent.modeListMap", "AI.agent.model.model_list_map");
            names = ParseDiscoveredModelNames(entries);
        }
        catch
        {
            // 缓存不可读 → 静态基线
        }
        return Task.FromResult<IReadOnlyList<ModelRegistration>?>(
            names is { Count: > 0 }
                ? names.Select(m => new ModelRegistration($"{Id}-{m}", m)).ToList()
                : null);
    }

    /// <summary>
    /// 解析两类缓存值（结构同形：{ 任意分组: [ { name: "模型名", … } ] }），
    /// 提取聊天模型 name，去重（大小写不敏感）并稳定排序。
    /// </summary>
    public static IReadOnlyList<string> ParseDiscoveredModelNames(IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        var found = new List<string>();
        foreach (var (_, json) in entries)
        {
            try
            {
                if (JsonNode.Parse(json) is not JsonObject root) continue;
                foreach (var group in root)
                {
                    if (group.Value is not JsonArray arr) continue;
                    foreach (var item in arr)
                    {
                        if (item?["name"] is not JsonValue v || !v.TryGetValue<string>(out var name)) continue;
                        if (IsChatModelName(name) && !found.Contains(name, StringComparer.OrdinalIgnoreCase))
                            found.Add(name);
                    }
                }
            }
            catch
            {
                // 单条缓存损坏忽略
            }
        }
        return found.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>聊天模型名过滤语义与 CodeBuddyAdapter.IsChatModel 一致（排除 default/auto/completion/custom-local 等）。</summary>
    private static bool IsChatModelName(string? modelId) => CodeBuddyAdapter.IsChatModel(modelId);

    /// <summary>构造 Trae 私有协议请求头：Cloud-IDE-JWT 鉴权 + IDE 身份伪装（每请求随机 machineId）。</summary>
    protected static Dictionary<string, string> BuildHeaders(AuthInfo auth)
    {
        var machineId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return new Dictionary<string, string>
        {
            ["Authorization"] = $"Cloud-IDE-JWT {auth.Token}",
            ["X-Cloudide-Token"] = auth.Token!,
            ["x-uid"] = auth.UserId ?? "",
            ["x-app-id"] = XAppId,
            ["x-device-id"] = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(machineId))).ToLowerInvariant()[..32],
            ["x-machine-id"] = machineId,
            ["x-request-id"] = Guid.NewGuid().ToString(),
            ["x-ide-version"] = IdeVersion,
            ["x-ide-version-code"] = IdeVersionCode,
            ["x-device-type"] = "windows",
            ["x-os-version"] = "Windows 10",
            ["Accept"] = "text/event-stream",
        };
    }

    /// <summary>把 OpenAI 请求体改写为 Trae 私有协议：string content → [{type:"text",text}]，注入 function 与会话 ID。</summary>
    protected JsonObject BuildUpstreamBody(JsonObject request)
    {
        var messages = new JsonArray();
        if (request["messages"] is JsonArray arr)
        {
            foreach (var m in arr)
            {
                if (m is not JsonObject msg) continue;
                var content = msg["content"];
                var mapped = (JsonObject)msg.DeepClone();
                if (content is JsonValue v && v.TryGetValue<string>(out var s))
                    mapped["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = s });
                messages.Add(mapped);
            }
        }

        var body = new JsonObject
        {
            ["messages"] = messages,
            ["model"] = request["model"]?.GetValue<string>(),
            ["function"] = ChatFunction,
            ["stream"] = true,
            ["request_id"] = Guid.NewGuid().ToString(),
            ["session_id"] = Guid.NewGuid().ToString(),
        };
        if (request["max_tokens"] is JsonValue mv && mv.TryGetValue<int>(out var maxTokens))
            body["max_tokens"] = maxTokens;
        return body;
    }

    /// <summary>把 Trae 私有 SSE 事件（event + data）归一化为 OpenAI chunk；未识别事件丢弃。</summary>
    public static IReadOnlyList<JsonObject> NormalizeTraeEvent(string? evt, string data, string model)
    {
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        switch (evt)
        {
            case "done":
            {
                var finish = "stop";
                try
                {
                    finish = (JsonNode.Parse(data) as JsonObject)?["finish_reason"]?.GetValue<string>() ?? "stop";
                }
                catch { /* 默认 stop */ }
                return new[] { Chunk(created, model, delta: new JsonObject(), finishReason: finish) };
            }
            case "output":
            {
                string? response = null;
                try
                {
                    response = (JsonNode.Parse(data) as JsonObject)?["response"]?.GetValue<string>();
                }
                catch { /* 忽略无法解析的行 */ }
                if (response is null) return Array.Empty<JsonObject>();
                return new[] { Chunk(created, model, new JsonObject { ["content"] = response }, null) };
            }
            default:
                return Array.Empty<JsonObject>();
        }
    }

    protected static JsonObject Chunk(long created, string model, JsonObject delta, string? finishReason) =>
        new()
        {
            ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = delta,
                ["finish_reason"] = finishReason is null ? null : JsonValue.Create(finishReason),
            }),
        };

    public abstract Task ChatAsync(JsonObject request, AdapterAccount? account, Func<JsonObject, ValueTask> emit, CancellationToken ct = default);

    /// <summary>向单个候选端点发请求并把 SSE 事件流转为 OpenAI chunk；返回 null 表示成功，否则为错误。</summary>
    protected async Task<Exception?> TryEndpointAsync(
        string endpoint, Dictionary<string, string> headers, JsonObject body, string model,
        Func<JsonObject, ValueTask> emit, CancellationToken ct)
    {
        try
        {
            using var res = await UpstreamHttp.PostJsonAsync($"{UpstreamBase}{endpoint}", headers, body, Timeout, ct);
            if ((int)res.StatusCode >= 400)
            {
                var errBody = await res.Content.ReadAsStringAsync(ct);
                return new TraeUpstreamException((int)res.StatusCode,
                    $"{Id} upstream {res.StatusCode}: {errBody[..Math.Min(200, errBody.Length)]}");
            }
            await foreach (var (evt, data) in res.ReadSseAsync(ct))
            {
                foreach (var chunk in NormalizeTraeEvent(evt, data, model))
                    await emit(chunk);
            }
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return e;
        }
    }
}

/// <summary>上游 HTTP 错误；StatusCode 为 401/403 时 IsAuthError=true，用于触发一次刷新重试。</summary>
public sealed class TraeUpstreamException : UpstreamException
{
    public TraeUpstreamException(int statusCode, string message) : base(statusCode, message) { }

    public bool IsAuthError => StatusCode is 401 or 403;
}
