using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>
/// SSE 统一出口 + 非流式聚合器。
/// 适配器只负责 emit OpenAI chunk，本模块负责：
///   1. 流式：把 chunk 序列化为 `data: {...}\n\n` 写回客户端，收尾补 `data: [DONE]`；
///   2. 非流式：把所有 chunk 聚合为单个 chat.completion，上游未给 usage 时按 chars/4 估算。
/// </summary>
public static class Sse
{
    /// <summary>流式：先把 SSE 响应头写出去，再逐 chunk 转发；无论成败都以 [DONE] 收尾。</summary>
    public static async Task StreamResponseAsync(
        HttpResponse response,
        Func<Func<JsonObject, ValueTask>, Task> run,
        CancellationToken ct = default)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        await response.StartAsync(ct);

        async ValueTask Emit(JsonObject chunk)
        {
            await response.WriteAsync($"data: {chunk.ToJsonString()}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }

        try
        {
            await run(Emit);
        }
        finally
        {
            await response.WriteAsync("data: [DONE]\n\n", ct);
            await response.Body.FlushAsync(ct);
        }
    }

    /// <summary>非流式：完整跑完适配器，把 chunk 聚合成单个 chat.completion 对象。</summary>
    public static async Task<JsonObject> CollectNonStreamingAsync(
        IAdapter adapter,
        JsonObject body,
        CancellationToken ct = default)
    {
        var chunks = new List<JsonObject>();
        await adapter.ChatAsync(body, chunk =>
        {
            chunks.Add(chunk);
            return ValueTask.CompletedTask;
        }, ct);

        var content = new System.Text.StringBuilder();
        string? finishReason = null;
        string id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..8];
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string model = "";
        JsonNode? usage = null;

        foreach (var c in chunks)
        {
            id = c["id"]?.GetValue<string>() ?? id;
            model = c["model"]?.GetValue<string>() ?? model;
            if (c["created"] is JsonValue cv && cv.TryGetValue<long>(out var cr)) created = cr;
            var choice = c["choices"] is JsonArray choices && choices.Count > 0 ? choices[0] as JsonObject : null;
            if (choice?["delta"] is JsonObject delta && delta["content"] is JsonValue dc)
                content.Append(dc.GetValue<string>());
            if (choice?["finish_reason"] is JsonValue fr && fr.TryGetValue<string>(out var frs) && frs is not null)
                finishReason = frs;
            if (c["usage"] is JsonObject u) usage = u.DeepClone();
        }

        if (usage is null)
        {
            usage = new JsonObject
            {
                ["prompt_tokens"] = EstimatePromptTokens(body?["messages"] as JsonArray),
                ["completion_tokens"] = (long)Math.Ceiling(content.Length / 4.0),
            };
        }

        return new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = content.ToString(),
                },
                ["finish_reason"] = finishReason ?? "stop",
            }),
            ["usage"] = usage,
        };
    }

    /// <summary>估算 prompt token：把消息数组序列化后按字符数 /4 折算。</summary>
    private static long EstimatePromptTokens(JsonArray? messages)
    {
        if (messages is null) return 0;
        long chars = 0;
        foreach (var m in messages)
        {
            if (m is not JsonObject msg) continue;
            var c = msg["content"];
            chars += c is JsonValue v && v.TryGetValue<string>(out var s) ? s.Length : c?.ToJsonString().Length ?? 0;
        }
        return (long)Math.Ceiling(chars / 4.0);
    }
}
