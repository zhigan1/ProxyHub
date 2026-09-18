namespace ProxyHub;

/// <summary>
/// 上游返回非 2xx 或上游链路不可用。StatusCode 决定该错误是否可通过"换模型/换账号"重试：
/// 5xx/429（过载限流）与 401/403（凭据失效，换账号可救）视为可重试；
/// 其余 4xx 视为调用方参数问题，换任何节点都无法挽救，直接透传。
/// </summary>
public class UpstreamException : Exception
{
    public int StatusCode { get; }

    public UpstreamException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public virtual bool IsRetryable => StatusCode is >= 500 or 429 or 401 or 403;
}
