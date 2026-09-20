using SqlSugar;

namespace ProxyHub;

/// <summary>
/// Token 用量持久化实体：按 平台+模型+账号+日期 唯一（唯一索引由 EnsureCreated 以原生 SQL 建立，
/// 避免绑定 SqlSugar 版本相关的 SugarIndexAttribute 签名），写入时 upsert 累加。
/// AccountId 为空语义（无账号/凭据缺失节点）统一落库为空字符串，规避 SQLite 唯一索引对 NULL 视为互异的坑。
/// </summary>
[SugarTable("token_usage_record", TableDescription = "Token 用量：平台×模型×账号×日期 粒度")]
public class TokenUsageRecord
{
    /// <summary>自增主键。SQLite 仅 INTEGER PRIMARY KEY 支持自增，故用 int（SqlSugar 约束）。</summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>平台适配器 ID（codebuddy / traecn / traework / qoder）。</summary>
    [SugarColumn(Length = 64)]
    public string AdapterId { get; set; } = "";

    /// <summary>底层真实上游模型 ID。</summary>
    [SugarColumn(Length = 128)]
    public string Model { get; set; } = "";

    /// <summary>账号标识；空字符串 = 无账号（对应链上 account=null 节点）。</summary>
    [SugarColumn(Length = 128)]
    public string AccountId { get; set; } = "";

    /// <summary>统计日期（UTC，yyyy-MM-dd）。</summary>
    [SugarColumn(Length = 10)]
    public string Date { get; set; } = "";

    [SugarColumn]
    public long Requests { get; set; }

    [SugarColumn]
    public long PromptTokens { get; set; }

    [SugarColumn]
    public long CompletionTokens { get; set; }

    [SugarColumn]
    public DateTime CreatedAt { get; set; }

    [SugarColumn]
    public DateTime UpdatedAt { get; set; }
}
