using System.Text.Json.Nodes;

namespace ProxyHub.Adapters;

/// <summary>
/// TraeWork 桌面版（TRAE SOLO CN）适配器。
/// 与 Trae CN 共享 tc 解密与上游主机，差异：
///   1. 凭据目录 %APPDATA%\TRAE SOLO CN；
///   2. chat function=solo_work_lite（轻排队）；
///   3. token 过期（401/403）时用本地 refreshToken 调 Trae OAuth ExchangeToken 自动换新并重试一次。
/// </summary>
public sealed class TraeWorkAdapter : TraeAdapterBase
{
    private const string AuthBase = "https://api.trae.cn";
    private const string SoloClientId = "en1oxy7wnw8j9n";

    private readonly string _authHost;

    public TraeWorkAdapter(TimeSpan? timeout = null, string? storageFile = null, string? authHost = null)
        : base(timeout)
    {
        StorageFile = storageFile ?? TraeStoragePath("TRAE SOLO CN");
        _authHost = authHost ?? AuthBase;
    }

    public override string Id => "traework";

    protected override string ChatFunction => "solo_work_lite";

    protected override string StorageFile { get; }

    /// <summary>
    /// 持久刷新：优先用 refreshToken 调 ExchangeToken 换新 token 并写回缓存（50 分钟 TTL）；
    /// 无 refreshToken 时退回重读磁盘（桌面端若在线会更新文件）。
    /// </summary>
    public override async Task<AuthInfo> RefreshAuthAsync(CancellationToken ct = default)
    {
        var current = await GetAuthAsync(ct);
        if (current.RefreshToken is null)
        {
            Cache.Invalidate("auth");
            return await GetAuthAsync(ct);
        }

        var fresh = await ExchangeTokenAsync(current.RefreshToken, current.UserId, ct);
        Cache.Set("auth", fresh, DateTimeOffset.UtcNow.AddMinutes(50));
        return fresh;
    }

    /// <summary>调用 Trae OAuth ExchangeToken，用 refreshToken 换新 token（SOLO CN ClientID）。</summary>
    private async Task<AuthInfo> ExchangeTokenAsync(string refreshToken, string? userId, CancellationToken ct)
    {
        var url = $"{_authHost}/cloudide/api/v3/trae/oauth/ExchangeToken";
        var body = new JsonObject
        {
            ["ClientID"] = SoloClientId,
            ["RefreshToken"] = refreshToken,
            ["ClientSecret"] = "-",
            ["UserID"] = userId,
        };
        using var res = await UpstreamHttp.PostJsonAsync(url, new Dictionary<string, string>(), body, Timeout, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if ((int)res.StatusCode >= 400)
            throw new HttpRequestException($"TraeWork ExchangeToken failed: {(int)res.StatusCode} {text[..Math.Min(200, text.Length)]}");

        var data = JsonNode.Parse(text) as JsonObject;
        var token = data?["token"]?.GetValue<string>()
            ?? data?["access_token"]?.GetValue<string>()
            ?? data?["accessToken"]?.GetValue<string>()
            ?? throw new InvalidDataException($"TraeWork ExchangeToken 响应缺少 token: {text[..Math.Min(200, text.Length)]}");
        return new AuthInfo(
            Token: token,
            RefreshToken: data?["refreshToken"]?.GetValue<string>() ?? data?["refresh_token"]?.GetValue<string>() ?? refreshToken,
            UserId: data?["userId"]?.GetValue<string>() ?? data?["uid"]?.GetValue<string>() ?? userId);
    }

    /// <summary>对话：401/403 时自动刷新一次凭据并重试，其余错误直接穿透所有候选端点。</summary>
    public override async Task ChatAsync(JsonObject request, Func<JsonObject, ValueTask> emit, CancellationToken ct = default)
    {
        var auth = await GetAuthAsync(ct);
        var headers = BuildHeaders(auth);
        var body = BuildUpstreamBody(request);
        var model = request["model"]?.GetValue<string>() ?? "";

        Exception? lastErr = null;
        var sawAuthError = false;
        foreach (var ep in ChatEndpoints)
        {
            lastErr = await TryEndpointAsync(ep, headers, body, model, emit, ct);
            if (lastErr is null) return;
            if (lastErr is TraeUpstreamException { IsAuthError: true }) sawAuthError = true;
        }

        if (sawAuthError)
        {
            var fresh = await RefreshAuthAsync(ct);
            headers["Authorization"] = $"Cloud-IDE-JWT {fresh.Token}";
            headers["X-Cloudide-Token"] = fresh.Token!;
            foreach (var ep in ChatEndpoints)
            {
                lastErr = await TryEndpointAsync(ep, headers, body, model, emit, ct);
                if (lastErr is null) return;
            }
        }

        throw lastErr ?? new InvalidOperationException("TraeWork 所有上游端点均失败");
    }
}
