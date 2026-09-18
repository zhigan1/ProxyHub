namespace ProxyHub;

/// <summary>
/// 运行时配置持有者：热更新后整体替换 Current，并向订阅方广播。
/// 端口/适配器开关属结构性配置，仅启动时读取；分组/熔断/账号即时生效。
/// </summary>
public sealed class RuntimeConfig
{
    private readonly object _gate = new();
    private ProxyHubConfig _current;

    public RuntimeConfig(ProxyHubConfig initial) => _current = initial;

    public event Action<ProxyHubConfig>? Changed;

    public ProxyHubConfig Current
    {
        get { lock (_gate) return _current; }
    }

    public void Apply(ProxyHubConfig cfg)
    {
        lock (_gate) _current = cfg;
        Changed?.Invoke(cfg);
    }

    /// <summary>API 鉴权：空 key 视为本地开放模式；否则要求 Bearer / x-api-key 形式匹配。</summary>
    public bool IsAuthorized(HttpContext ctx)
    {
        var key = Current.ProxyKey;
        if (string.IsNullOrEmpty(key)) return true;
        var h = ctx.Request.Headers.Authorization.ToString();
        return h == $"Bearer {key}" || h == $"x-api-key {key}";
    }
}
