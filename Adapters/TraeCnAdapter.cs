using System.Text.Json.Nodes;

namespace ProxyHub.Adapters;

/// <summary>
/// Trae 国内版（CN IDE）适配器。
/// 凭据：自动解密 %APPDATA%\Trae CN\User\globalStorage\storage.json 中的 iCubeAuthInfo（tc 算法），
/// 桌面端负责刷新 token，网关侧缓存 10 分钟。chat function=inline_chat。
/// </summary>
public sealed class TraeCnAdapter : TraeAdapterBase
{
    public TraeCnAdapter(TimeSpan? timeout = null, string? storageFile = null) : base(timeout)
    {
        StorageFile = storageFile ?? TraeStoragePath("Trae CN");
    }

    public override string Id => "traecn";

    protected override string ChatFunction => "inline_chat";

    protected override string StorageFile { get; }

    public override async Task ChatAsync(JsonObject request, Func<JsonObject, ValueTask> emit, CancellationToken ct = default)
    {
        var auth = await GetAuthAsync(ct);
        var headers = BuildHeaders(auth);
        var body = BuildUpstreamBody(request);
        var model = request["model"]?.GetValue<string>() ?? "";

        Exception? lastErr = null;
        foreach (var ep in ChatEndpoints)
        {
            lastErr = await TryEndpointAsync(ep, headers, body, model, emit, ct);
            if (lastErr is null) return;
        }
        throw lastErr ?? new InvalidOperationException("Trae CN 所有上游端点均失败");
    }
}
