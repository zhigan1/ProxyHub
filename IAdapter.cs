using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>模型注册项：对外 ID（{平台}-{模型}）与上游真实 ID 的映射。</summary>
public sealed record ModelRegistration(string ExternalId, string UpstreamId);

/// <summary>对外模型列表项。</summary>
public sealed record ModelInfo(string Id, string OwnedBy);

/// <summary>统一凭据形状：各平台字段不同，任一字段非空即视为"已就绪"。</summary>
public sealed record AuthInfo(
    string? Token = null,
    string? RefreshToken = null,
    string? UserId = null,
    string? Uid = null,
    string? Pat = null,
    bool OAuth = false)
{
    public bool HasCredential =>
        Token is not null || UserId is not null || Uid is not null || Pat is not null || OAuth;
}

/// <summary>
/// 平台适配器契约（五职责：accounts / auth / models / request / stream）。
/// ChatAsync 以"emit 回调"方式吐出 OpenAI chunk，由网关统一序列化为 SSE 或聚合为非流式响应。
/// 多账号：account 为 null 表示该适配器的默认账号（通常是第一个发现的）。
/// </summary>
public interface IAdapter
{
    string Id { get; }

    /// <summary>枚举本机可发现的全部账号（含默认账号）；凭据缺失/扫描失败返回空列表而非抛错。</summary>
    Task<IReadOnlyList<AdapterAccount>> DiscoverAccountsAsync(CancellationToken ct = default);

    /// <summary>读取（并缓存）指定账号凭据；缺失时抛异常（Fail Fast），由故障转移链换下一账号。</summary>
    Task<AuthInfo> GetAuthAsync(AdapterAccount? account = null, CancellationToken ct = default);

    /// <summary>刷新指定账号凭据（失效缓存后重读，或走 OAuth ExchangeToken 换新）。</summary>
    Task<AuthInfo> RefreshAuthAsync(AdapterAccount? account = null, CancellationToken ct = default);

    /// <summary>静态基线模型映射：网关启动即生效，作为动态拉取失败时的兜底。</summary>
    IReadOnlyList<ModelRegistration> RegisterModels();

    /// <summary>
    /// 可选动态模型源：从平台实时拉取"当前账号可用"的模型映射。
    /// 返回 null 表示不支持或拉取失败（由 Registry 静默回退到 RegisterModels 的静态基线）。
    /// 默认不动态拉取。
    /// </summary>
    Task<IReadOnlyList<ModelRegistration>?> FetchModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ModelRegistration>?>(null);

    /// <summary>
    /// 以指定账号执行一次对话：request 为已替换上游 model 且 stream=true 的 OpenAI 请求体；
    /// 每个增量以 OpenAI chat.completion.chunk（JsonObject）形式 emit。
    /// </summary>
    Task ChatAsync(JsonObject request, AdapterAccount? account, Func<JsonObject, ValueTask> emit, CancellationToken ct = default);
}
