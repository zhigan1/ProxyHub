using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace ProxyHub;

/// <summary>
/// 用量统计：按 适配器+模型+日期 累计请求数与 token。内存聚合即时返回（Summary 结构保持兼容）；
/// 配置了持久化存储时，同一条记录经 Channel 投递给单一后台工作者批量事务写入 SQLite（请求路径零 IO 等待）。
/// 上游未返回 usage 时按内容长度估算（chars/4）。
/// </summary>
public sealed class UsageTracker : IAsyncDisposable
{
    private sealed class Counter
    {
        public long Requests;
        public long PromptTokens;
        public long CompletionTokens;
    }

    private const int BatchSize = 256;

    private readonly ConcurrentDictionary<string, Counter> _store = new();
    private readonly TokenUsageStore? _persist;
    private readonly Channel<TokenUsageRecord> _channel;
    private readonly Task? _worker;

    /// <summary>持久化存储（未配置时为 null，仅内存聚合）。</summary>
    public TokenUsageStore? Store => _persist;

    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    public UsageTracker(TokenUsageStore? persist = null)
    {
        _persist = persist;
        _channel = Channel.CreateUnbounded<TokenUsageRecord>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        if (_persist is not null)
            _worker = Task.Run(RunWorkerAsync);
    }

    public void Record(string adapterId, string model, long? promptTokens, long? completionTokens, int contentLength = 0, string? accountId = null)
    {
        var estimated = (long)Math.Ceiling(contentLength / 4.0);
        var prompt = promptTokens ?? estimated;
        var completion = completionTokens ?? estimated;
        var today = Today();
        var counter = _store.GetOrAdd($"{adapterId}|{model}|{today}", _ => new Counter());
        lock (counter)
        {
            counter.Requests += 1;
            counter.PromptTokens += prompt;
            counter.CompletionTokens += completion;
        }

        // 持久化投递：非阻塞 TryWrite，写库延迟由后台工作者承担
        if (_persist is not null)
        {
            _channel.Writer.TryWrite(new TokenUsageRecord
            {
                AdapterId = adapterId,
                Model = model,
                AccountId = accountId ?? "",
                Date = today,
                Requests = 1,
                PromptTokens = prompt,
                CompletionTokens = completion,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
    }

    /// <summary>停止接收新记录并等待后台队列全部落库（应用退出 / 测试收尾时调用；幂等，调用后本实例不再投递）。</summary>
    public async Task FlushAsync()
    {
        _channel.Writer.TryComplete();
        if (_worker is not null) await _worker.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await FlushAsync().ConfigureAwait(false);

    /// <summary>单一后台消费者：唤醒即取走当前积压的一批（≤256 条），单事务落库；队列排空且写入端完成后自然退出。</summary>
    private async Task RunWorkerAsync()
    {
        var reader = _channel.Reader;
        while (true)
        {
            try
            {
                if (!await reader.WaitToReadAsync().ConfigureAwait(false)) return; // 写入端已完成且队列已排空
                var batch = new List<TokenUsageRecord>(BatchSize);
                while (batch.Count < BatchSize && reader.TryRead(out var rec))
                    batch.Add(rec);
                try
                {
                    await _persist!.UpsertAsync(batch).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[Usage] 用量持久化失败（{batch.Count} 条）：{e.Message}");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Usage] 用量消费循环异常：{e.Message}");
            }
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

    /// <summary>内存明细行（供聚合回退路径使用；无账号维度，AccountId 恒为空）。</summary>
    public IReadOnlyList<UsageRow> RawRows()
    {
        var rows = new List<UsageRow>(_store.Count);
        foreach (var (key, v) in _store)
        {
            long requests, prompt, completion;
            lock (v) { requests = v.Requests; prompt = v.PromptTokens; completion = v.CompletionTokens; }
            var parts = key.Split('|');
            rows.Add(new UsageRow(parts[0], parts[1], "", parts[2], requests, prompt, completion));
        }
        return rows;
    }

    /// <summary>聚合查询（内存回退路径，source=memory）：totals / byPlatform / topModels / byDay，可选日期闭区间。</summary>
    public object Aggregate(string? from = null, string? to = null, int top = 10) =>
        UsageAggregates.Summarize(RawRows(), from, to, top, "memory");
}
