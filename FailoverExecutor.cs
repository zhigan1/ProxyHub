using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>chunk 出口抽象。SSE 直写型在首块写出后即"已提交"（HTTP 头已发出，无法再换源）；收集型永远可回滚。</summary>
public interface IChunkSink
{
    bool Committed { get; }

    /// <summary>executor 在每次尝试新候选前调用：收集型清空缓冲，SSE 型无操作（靠 Committed 判定）。</summary>
    ValueTask BeginAttemptAsync();

    ValueTask WriteAsync(JsonObject chunk, CancellationToken ct);

    /// <summary>流中断收尾：error chunk + [DONE]。仅在已提交（SSE 型）时有效，收集型为 no-op。</summary>
    Task WriteErrorAsync(string message, CancellationToken ct);

    /// <summary>切换候选时由 executor 更新；SSE 型在提交响应头时写出 X-ProxyHub-Failover。</summary>
    string? FailoverNote { get; set; }

    /// <summary>流中最后一个 usage 对象（上游末块通常携带），用于用量记账。</summary>
    JsonObject? Usage { get; }

    /// <summary>累计增量文本长度（估算 completion tokens 用）。</summary>
    long ContentChars { get; }
}

/// <summary>
/// SSE 直写出口：首块到达才写响应头 —— 这之前失败仍可换下一候选（客户端无感知）；
/// 提交后断流只能补 error chunk + [DONE] 收尾（行业标准做法，主流客户端可正常渲染）。
/// </summary>
public sealed class SseSink : IChunkSink
{
    private readonly HttpResponse _response;
    private bool _done;

    public bool Committed { get; private set; }
    public string? FailoverNote { get; set; }
    public JsonObject? Usage { get; private set; }
    public long ContentChars { get; private set; }

    public SseSink(HttpResponse response) => _response = response;

    public ValueTask BeginAttemptAsync() => ValueTask.CompletedTask;

    public async ValueTask WriteAsync(JsonObject chunk, CancellationToken ct)
    {
        if (!Committed)
        {
            if (FailoverNote is not null)
                _response.Headers["X-ProxyHub-Failover"] = Uri.EscapeDataString(FailoverNote); // Kestrel 拒绝非 ASCII 头值
            _response.StatusCode = StatusCodes.Status200OK;
            _response.ContentType = "text/event-stream";
            _response.Headers.CacheControl = "no-cache";
            _response.Headers.Connection = "keep-alive";
            await _response.StartAsync(ct);
            Committed = true;
        }
        await _response.WriteAsync($"data: {chunk.ToJsonString()}\n\n", ct);
        await _response.Body.FlushAsync(ct);
        Track(chunk);
    }

    /// <summary>成功收尾。未提交（空响应）时为 no-op，避免给空流写 200 头。</summary>
    public async Task WriteDoneAsync(CancellationToken ct)
    {
        if (!Committed || _done) return;
        _done = true;
        await _response.WriteAsync("data: [DONE]\n\n", ct);
        await _response.Body.FlushAsync(ct);
    }

    /// <summary>流中断收尾：error chunk + [DONE]。仅在已提交时有效。</summary>
    public async Task WriteErrorAsync(string message, CancellationToken ct)
    {
        if (!Committed || _done) return;
        _done = true;
        var errorChunk = new JsonObject
        {
            ["object"] = "chat.completion.chunk",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = new JsonObject { ["content"] = $"\n\n[ProxyHub] 上游流中断：{message}" },
                ["finish_reason"] = "error",
            }),
        };
        await _response.WriteAsync($"data: {errorChunk.ToJsonString()}\n\n", ct);
        await _response.WriteAsync("data: [DONE]\n\n", ct);
        await _response.Body.FlushAsync(ct);
    }    private void Track(JsonObject chunk)
    {
        if (chunk["usage"] is JsonObject u) Usage = u;
        if (chunk["choices"]?[0]?["delta"]?["content"] is JsonValue v && v.TryGetValue<string>(out var s))
            ContentChars += s.Length;
    }
}

/// <summary>收集出口：非流式聚合用。不向客户端写字节，故任意阶段失败都可整链重试。</summary>
public sealed class CollectingSink : IChunkSink
{
    public List<JsonObject> Chunks { get; } = new();

    public bool Committed => false;
    public string? FailoverNote { get; set; }
    public JsonObject? Usage { get; private set; }
    public long ContentChars { get; private set; }

    public ValueTask BeginAttemptAsync()
    {
        Chunks.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(JsonObject chunk, CancellationToken ct)
    {
        Chunks.Add(chunk);
        if (chunk["usage"] is JsonObject u) Usage = u;
        if (chunk["choices"]?[0]?["delta"]?["content"] is JsonValue v && v.TryGetValue<string>(out var s))
            ContentChars += s.Length;
        return ValueTask.CompletedTask;
    }

    /// <summary>收集型永不提交，无流可收尾：no-op。</summary>
    public Task WriteErrorAsync(string message, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>候选链全部失败（或流提交后中断）。</summary>
public sealed class ChainExhaustedException : Exception
{
    public IReadOnlyList<string> Attempts { get; }

    public ChainExhaustedException(IReadOnlyList<string> attempts)
        : base("所有候选节点均失败：" + string.Join("；", attempts)) => Attempts = attempts;
}

/// <summary>
/// 故障转移执行器：沿候选链逐节点执行，跳过熔断开启的节点；
/// 首 chunk 提交前失败 → 熔断该节点并换下一候选（模型切换 + 账号切换）；
/// 提交后失败 → 记熔断并补 error chunk 收尾（不再换源，响应头已不可变）；
/// 非重试类错误（参数错误等 4xx）→ 立即透传，不浪费候选。
/// </summary>
public sealed class FailoverExecutor
{
    private readonly CircuitBreakerRegistry _breakers;

    public FailoverExecutor(CircuitBreakerRegistry breakers) => _breakers = breakers;

    public CircuitBreakerRegistry Breakers => _breakers;

    /// <summary>返回实际服务的节点（调用方据此记账用量）。</summary>
    public async Task<ChainNode> ExecuteAsync(
        IReadOnlyList<ChainNode> chain,
        JsonObject request,
        IChunkSink sink,
        CancellationToken ct = default)
    {
        var tried = new List<string>();

        for (var i = 0; i < chain.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var node = chain[i];

            if (!_breakers.TryEnter(node.BreakerKey))
            {
                tried.Add($"{node.Label} [熔断中，跳过]");
                continue;
            }

            if (i > 0 && tried.Count > 0)
                sink.FailoverNote = $"{tried[^1]} → {node.Label}";

            await sink.BeginAttemptAsync();
            try
            {
                var body = (JsonObject)request.DeepClone();
                body["model"] = node.UpstreamId;
                body["stream"] = true; // 网关内部恒走流式，出口再按需聚合

                await node.Adapter.ChatAsync(
                    body, node.Account,
                    chunk => sink.WriteAsync(chunk, ct),
                    ct);

                _breakers.RecordSuccess(node.BreakerKey);
                return node;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 客户端主动断开，不做任何补救
            }
            catch (Exception e) when (sink.Committed)
            {
                // 已向客户端写出字节，无法换源：记录熔断 + 补 error chunk 收尾
                _breakers.RecordFailure(node.BreakerKey, e.Message);
                await sink.WriteErrorAsync(e.Message, CancellationToken.None);
                throw new ChainExhaustedException(new[] { $"{node.Label}: {e.Message}（流已提交，不再切换）" });
            }
            catch (Exception e) when (IsRetryable(e, ct))
            {
                _breakers.RecordFailure(node.BreakerKey, e.Message);
                tried.Add($"{node.Label}: {e.Message}");
            }
            // 其余（非重试类，如参数 4xx）直接向上抛，由 HTTP 层透传状态码
        }

        throw new ChainExhaustedException(tried.Count > 0
            ? tried
            : new[] { "候选链为空或全部节点处于熔断状态" });
    }

    /// <summary>分类：上游 5xx/429/401/403 与一切网络/超时/未知错误视为可切换；客户端取消不算。</summary>
    public static bool IsRetryable(Exception e, CancellationToken ct)
    {
        if (e is OperationCanceledException) return !ct.IsCancellationRequested; // 仅超时触发的取消可重试
        if (e is UpstreamException u) return u.IsRetryable;
        // HttpRequestException / TimeoutException / IOException / CLI 崩溃 / 凭据缺失等：换节点也许能救
        return true;
    }
}
