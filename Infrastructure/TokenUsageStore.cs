using SqlSugar;

namespace ProxyHub;

/// <summary>
/// Token 用量 SQLite 存储（SqlSugar）：建表 + 事务内批量 upsert 累加 + 日期范围查询/聚合。
/// 由单一后台消费者串行调用，IsAutoCloseConnection 短连接模式，不长期占用 SQLite 文件锁。
/// </summary>
public sealed class TokenUsageStore
{
    private readonly SqlSugarClient _db;

    public string DbPath { get; }

    public TokenUsageStore(string dbPath)
    {
        DbPath = dbPath;
        var full = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={full}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
    }

    /// <summary>建库建表 + 幂等建立 平台+模型+账号+日期 唯一索引；可重复调用。</summary>
    public void EnsureCreated()
    {
        _db.CodeFirst.InitTables<TokenUsageRecord>();
        _db.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS uk_token_usage_dim ON token_usage_record (AdapterId, Model, AccountId, Date)");
    }

    /// <summary>
    /// 批量 upsert：先合并同键记录，再在单个事务内逐键"累加更新，不存在则插入"。
    /// 仅由单一后台消费者调用，天然串行，无并发写竞争。
    /// </summary>
    public async Task UpsertAsync(IReadOnlyList<TokenUsageRecord> records)
    {
        if (records.Count == 0) return;

        var now = DateTime.UtcNow;
        var merged = records
            .GroupBy(r => (r.AdapterId, r.Model, r.AccountId, r.Date))
            .Select(g => new TokenUsageRecord
            {
                AdapterId = g.Key.AdapterId,
                Model = g.Key.Model,
                AccountId = g.Key.AccountId,
                Date = g.Key.Date,
                Requests = g.Sum(x => x.Requests),
                PromptTokens = g.Sum(x => x.PromptTokens),
                CompletionTokens = g.Sum(x => x.CompletionTokens),
                CreatedAt = now,
                UpdatedAt = now,
            })
            .ToList();

        _db.Ado.BeginTran();
        try
        {
            foreach (var rec in merged)
            {
                var updated = await _db.Updateable<TokenUsageRecord>()
                    .Where(r => r.AdapterId == rec.AdapterId && r.Model == rec.Model
                                && r.AccountId == rec.AccountId && r.Date == rec.Date)
                    .SetColumns(r => new TokenUsageRecord
                    {
                        Requests = r.Requests + rec.Requests,
                        PromptTokens = r.PromptTokens + rec.PromptTokens,
                        CompletionTokens = r.CompletionTokens + rec.CompletionTokens,
                        UpdatedAt = rec.UpdatedAt,
                    })
                    .ExecuteCommandAsync();
                if (updated == 0)
                    await _db.Insertable(rec).ExecuteCommandAsync();
            }
            _db.Ado.CommitTran();
        }
        catch
        {
            _db.Ado.RollbackTran();
            throw;
        }
    }

    /// <summary>按日期范围查询明细（from/to 为 yyyy-MM-dd 闭区间，均可空）。</summary>
    public async Task<List<TokenUsageRecord>> QueryAsync(string? from = null, string? to = null)
    {
        var query = _db.Queryable<TokenUsageRecord>();
        if (!string.IsNullOrEmpty(from)) query = query.Where("date >= @from", new { @from });
        if (!string.IsNullOrEmpty(to)) query = query.Where("date <= @to", new { to });
        return await query.ToListAsync();
    }

    /// <summary>聚合查询：totals / byPlatform / topModels / byDay（source=sqlite）。</summary>
    public async Task<object> AggregateAsync(string? from = null, string? to = null, int top = 10)
    {
        var records = await QueryAsync(from, to);
        return UsageAggregates.Summarize(
            records.Select(r => new UsageRow(r.AdapterId, r.Model, r.AccountId, r.Date, r.Requests, r.PromptTokens, r.CompletionTokens)).ToList(),
            from, to, top, "sqlite");
    }
}
