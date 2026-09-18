using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ProxyHub;

/// <summary>故障转移候选节点：适配器 × 账号 × 上游模型，熔断键由此三元组构成。</summary>
public sealed record ChainNode(IAdapter Adapter, AdapterAccount? Account, string UpstreamId, string ExternalId)
{
    public string BreakerKey => $"{Adapter.Id}|{Account?.AccountId ?? "default"}|{UpstreamId}";

    public string Label => $"{ExternalId}@{Account?.AccountId ?? "default"}";

    public static ChainNode For(IAdapter adapter, string upstreamId, string externalId, AdapterAccount? account = null) =>
        new(adapter, account, upstreamId, externalId);
}

/// <summary>
/// 虚拟模型分组：在真实模型之上叠加一层"auto-xxx"虚拟 ID，分组定义全部来自配置（可增删改、热更新）。
/// match 模式作用于 Registry 当前生效模型表（动态拉取优先）→ 平台上新模型自动被分组收录。
/// </summary>
public sealed class ModelGroups
{
    private volatile IReadOnlyDictionary<string, GroupConfig> _groups;
    private static readonly ConcurrentDictionary<string, Regex> GlobCache = new();

    public ModelGroups(IReadOnlyDictionary<string, GroupConfig> initial) => _groups = initial;

    public void Update(IReadOnlyDictionary<string, GroupConfig> groups) => _groups = groups;

    public IEnumerable<string> Names() => _groups.Keys.OrderBy(k => k, StringComparer.Ordinal);

    public bool IsGroup(string modelId) => _groups.ContainsKey(modelId);

    public GroupConfig? Get(string modelId) => _groups.TryGetValue(modelId, out var g) ? g : null;

    public IReadOnlyDictionary<string, GroupConfig> All => _groups;

    /// <summary>
    /// 寻找与模型名匹配的最优先虚拟分组（优先非全通配的特化分组）。
    /// 例如客户端请求 "deepseek-v4.1-flash" 时，自动匹配到 "deepseek-v4.1-flash-auto" 分组并跨平台故障转移。
    /// </summary>
    public string? FindMatchingGroup(string modelId)
    {
        if (_groups.ContainsKey(modelId)) return modelId;

        // 1. 优先匹配专属特化分组（排除单 "*" 兜底分组）
        foreach (var (name, g) in _groups)
        {
            if (g.Match.Count == 1 && g.Match[0] == "*") continue;
            if (g.Match.Any(p => GlobMatch(p, modelId)))
                return name;
        }

        return null;
    }

    /// <summary>
    /// 展开分组为有序候选链，按账号轮次构建：
    /// 第 1 轮依 prefer 平台序（未列出的平台按注册顺序殿后）逐平台尝试其第一个账号的全部匹配模型
    /// → 所有平台第一账号耗尽后才进入第 2 轮备用账号，依此类推。
    /// 每平台每账号下的匹配模型保持名称稳定排序；无发现账号的平台作为第 1 轮的 account=null 节点，
    /// 让凭据失败进入熔断/切换链而非直接报错。
    /// </summary>
    public List<ChainNode> Expand(string group, Registry registry, AccountRegistry accounts, IReadOnlyList<IAdapter> adapterOrder)
    {
        if (!_groups.TryGetValue(group, out var g)) return new List<ChainNode>();

        var ordered = adapterOrder
            .Select((a, idx) => (Adapter: a, PreferIdx: IndexOfIgnoreCase(g.Prefer, a.Id), RegIdx: idx))
            .OrderBy(x => x.PreferIdx < 0 ? int.MaxValue : x.PreferIdx)
            .ThenBy(x => x.RegIdx)
            .Select(x => x.Adapter)
            .ToList();

        var perAdapter = ordered
            .Select(a =>
            {
                var models = registry.EffectiveModelsOf(a.Id)
                    .Where(m => g.Match.Any(p => GlobMatch(p, m.UpstreamId)))
                    .OrderBy(m => m.UpstreamId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var accs = accounts.AccountsOf(a.Id).ToList();
                if (accs.Count == 0) accs.Add(null!); // 无账号：第 1 轮 null 节点
                return (Adapter: a, Models: models, Accounts: accs);
            })
            .Where(x => x.Models.Count > 0)
            .ToList();

        var nodes = new List<ChainNode>();
        var maxRounds = perAdapter.Count == 0 ? 0 : perAdapter.Max(x => x.Accounts.Count);
        for (var round = 0; round < maxRounds; round++)
        {
            foreach (var x in perAdapter)
            {
                if (round >= x.Accounts.Count) continue; // 该平台账号数少于当前轮次
                foreach (var m in x.Models)
                    nodes.Add(new ChainNode(x.Adapter, x.Accounts[round], m.UpstreamId, m.ExternalId));
            }
        }
        return nodes;
    }

    private static int IndexOfIgnoreCase(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>通配符匹配：'*' 任意串，大小写不敏感。带编译缓存。</summary>
    public static bool GlobMatch(string pattern, string value)
    {
        var regex = GlobCache.GetOrAdd(pattern, p => new Regex(
            "^" + string.Join(".*", p.Split('*').Select(Regex.Escape)) + "$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase));
        return regex.IsMatch(value);
    }
}
