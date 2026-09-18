namespace ProxyHub;

/// <summary>
/// 适配器账号：一个平台可登录多个账号，每个账号独立凭据、独立熔断。
/// SourceFile / Pat 由各适配器解释（CodeBuddy=auth 信息文件，Trae=storage.json，Qoder=PAT）。
/// </summary>
public sealed record AdapterAccount(
    string AdapterId,
    string AccountId,
    string Label,
    string? SourceFile = null,
    string? Pat = null,
    bool Discovered = true,
    string? UserId = null,
    int? Credits = null,
    bool Enabled = true,
    int Order = 0)
{
    public override string ToString() => $"{AdapterId}:{AccountId}";
}
