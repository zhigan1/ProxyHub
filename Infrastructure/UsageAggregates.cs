namespace ProxyHub;

/// <summary>聚合中间行：平台 × 模型 × 账号 × 日期 粒度的用量明细（内存与 SQLite 共用）。</summary>
public sealed record UsageRow(
    string AdapterId,
    string Model,
    string AccountId,
    string Date,
    long Requests,
    long PromptTokens,
    long CompletionTokens);

/// <summary>
/// 用量聚合：同一份输入行集合产出前端约定结构 —— totals / byPlatform / topModels / byDay。
/// SQLite 持久化路径与内存回退路径共用，保证两种 source 下返回结构一致。
/// </summary>
public static class UsageAggregates
{
    public static object Summarize(IReadOnlyList<UsageRow> rows, string? from, string? to, int top, string source)
    {
        var range = rows
            .Where(r => (from is null || string.CompareOrdinal(r.Date, from) >= 0)
                     && (to is null || string.CompareOrdinal(r.Date, to) <= 0))
            .ToList();

        long requests = 0, prompt = 0, completion = 0;
        var platforms = new Dictionary<string, (long Requests, long Prompt, long Completion)>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, (long Requests, long Tokens)>(StringComparer.OrdinalIgnoreCase);
        var days = new SortedDictionary<string, (long Requests, long Tokens)>(StringComparer.Ordinal);

        foreach (var r in range)
        {
            requests += r.Requests;
            prompt += r.PromptTokens;
            completion += r.CompletionTokens;

            platforms.TryGetValue(r.AdapterId, out var p);
            platforms[r.AdapterId] = (p.Requests + r.Requests, p.Prompt + r.PromptTokens, p.Completion + r.CompletionTokens);

            models.TryGetValue(r.Model, out var m);
            models[r.Model] = (m.Requests + r.Requests, m.Tokens + r.PromptTokens + r.CompletionTokens);

            days.TryGetValue(r.Date, out var d);
            days[r.Date] = (d.Requests + r.Requests, d.Tokens + r.PromptTokens + r.CompletionTokens);
        }

        var take = Math.Clamp(top, 1, 100);
        return new
        {
            source,
            from,
            to,
            totals = new { requests, promptTokens = prompt, completionTokens = completion, tokens = prompt + completion },
            byPlatform = platforms
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new
                {
                    adapterId = kv.Key,
                    requests = kv.Value.Requests,
                    promptTokens = kv.Value.Prompt,
                    completionTokens = kv.Value.Completion,
                    tokens = kv.Value.Prompt + kv.Value.Completion,
                }).ToArray(),
            topModels = models
                .OrderByDescending(kv => kv.Value.Tokens)
                .ThenByDescending(kv => kv.Value.Requests)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .Select(kv => new { model = kv.Key, requests = kv.Value.Requests, tokens = kv.Value.Tokens })
                .ToArray(),
            byDay = days
                .Select(kv => new { date = kv.Key, requests = kv.Value.Requests, tokens = kv.Value.Tokens })
                .ToArray(),
        };
    }
}
