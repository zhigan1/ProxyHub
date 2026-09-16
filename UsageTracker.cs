using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace ProxyHub;

/// <summary>
/// 用量统计：按 适配器+模型+日期 累计请求数与 token。纯内存。
/// 对应上游 usage.js。上游未返回 usage 时按内容长度估算（chars/4）。
/// </summary>
public sealed class UsageTracker
{
    private sealed class Counter
    {
        public long Requests;
        public long PromptTokens;
        public long CompletionTokens;
    }

    private readonly ConcurrentDictionary<string, Counter> _store = new();

    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    public void Record(string adapterId, string model, long? promptTokens, long? completionTokens, int contentLength = 0)
    {
        var estimated = (long)Math.Ceiling(contentLength / 4.0);
        var prompt = promptTokens ?? estimated;
        var completion = completionTokens ?? estimated;
        var counter = _store.GetOrAdd($"{adapterId}|{model}|{Today()}", _ => new Counter());
        lock (counter)
        {
            counter.Requests += 1;
            counter.PromptTokens += prompt;
            counter.CompletionTokens += completion;
        }
    }

    /// <summary>汇总：byAdapter（按平台+模型）+ byDay（每日趋势，按日期升序）。</summary>
    public object Summary()
    {
        var byAdapter = new Dictionary<string, object>();
        var byDay = new SortedDictionary<string, (long Requests, long Tokens)>(StringComparer.Ordinal);

        foreach (var (key, v) in _store)
        {
            long requests, prompt, completion;
            lock (v) { requests = v.Requests; prompt = v.PromptTokens; completion = v.CompletionTokens; }

            var parts = key.Split('|');
            var (adapterId, model, date) = (parts[0], parts[1], parts[2]);

            if (!byAdapter.TryGetValue(adapterId, out var adObj))
            {
                adObj = new AdapterBucket();
                byAdapter[adapterId] = adObj;
            }
            var ad = (AdapterBucket)adObj;
            ad.Requests += requests;
            ad.PromptTokens += prompt;
            ad.CompletionTokens += completion;
            if (!ad.Models.TryGetValue(model, out var mm)) ad.Models[model] = mm = new ModelBucket();
            mm.Requests += requests;
            mm.Tokens += prompt + completion;

            byDay.TryGetValue(date, out var day);
            byDay[date] = (day.Requests + requests, day.Tokens + prompt + completion);
        }

        return new
        {
            byAdapter,
            byDay = byDay.Select(kv => new { date = kv.Key, requests = kv.Value.Requests, tokens = kv.Value.Tokens }).ToArray(),
        };
    }

    private sealed class AdapterBucket
    {
        [JsonPropertyName("requests")] public long Requests { get; set; }
        [JsonPropertyName("promptTokens")] public long PromptTokens { get; set; }
        [JsonPropertyName("completionTokens")] public long CompletionTokens { get; set; }
        [JsonPropertyName("models")] public Dictionary<string, ModelBucket> Models { get; } = new();
    }

    private sealed class ModelBucket
    {
        [JsonPropertyName("requests")] public long Requests { get; set; }
        [JsonPropertyName("tokens")] public long Tokens { get; set; }
    }
}
