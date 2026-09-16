using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>
/// 上游 HTTP 传输层：单例 HttpClient（连接池复用）+ 按请求超时 + 增量式 SSE 行解析。
/// 对应上游各适配器内的 postJson / postJsonBuffer，但有一个关键改进：
/// 上游 Node 版会把整个上游响应缓冲完再解析；这里用 ResponseHeadersRead + 逐行读取，
/// 实现真正的流式反代（首个 token 即时下发）。
/// </summary>
public static class UpstreamHttp
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // 超时由每请求 CancellationTokenSource 控制
    };

    /// <summary>POST JSON，返回已读出响应头的响应（body 为未消费的流）。调用方负责 Dispose。</summary>
    public static async Task<HttpResponseMessage> PostJsonAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        JsonObject body,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        foreach (var (k, v) in headers)
        {
            if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue; // 由 Content 提供
            req.Headers.TryAddWithoutValidation(k, v);
        }
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            return await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("upstream timeout");
        }
    }

    /// <summary>增量解析 SSE 流：逐行产出 (event, data)。未带 event: 的行 event 为 null。</summary>
    public static async IAsyncEnumerable<(string? Event, string Data)> ReadSseAsync(
        this HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? currentEvent = null;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            var t = line.Trim();
            if (t.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = t[6..].Trim();
            }
            else if (t.StartsWith("data:", StringComparison.Ordinal))
            {
                yield return (currentEvent, t[5..].Trim());
            }
        }
    }
}
