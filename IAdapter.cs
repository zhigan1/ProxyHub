using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>模型注册项：对外 ID（{平台}-{模型}）与上游真实 ID 的映射。</summary>
public sealed record ModelRegistration(string ExternalId, string UpstreamId);

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
/// 平台适配器契约（四职责：auth / models / request / stream）。
/// ChatAsync 以"emit 回调"方式吐出 OpenAI chunk，由网关统一序列化为 SSE 或聚合为非流式响应。
/// </summary>
public interface IAdapter
{
    string Id { get; }

    /// <summary>读取（并缓存）本机登录凭据；缺失时抛异常（Fail Fast）。</summary>
    Task<AuthInfo> GetAuthAsync(CancellationToken ct = default);

    /// <summary>刷新凭据（失效缓存后重读，或走 OAuth ExchangeToken 换新）。</summary>
    Task<AuthInfo> RefreshAuthAsync(CancellationToken ct = default);

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
    /// 执行一次对话：request 为已替换上游 model 且 stream=true 的 OpenAI 请求体；
    /// 每个增量以 OpenAI chat.completion.chunk（JsonObject）形式 emit。
    /// </summary>
    Task ChatAsync(JsonObject request, Func<JsonObject, ValueTask> emit, CancellationToken ct = default);
}
