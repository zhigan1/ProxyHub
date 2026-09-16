namespace ProxyHub;

/// <summary>
/// 适配器注册表：按模型对外 ID 路由到适配器，维护 对外ID ↔ 上游ID 映射。
/// 对应上游 registry.js。
/// </summary>
public sealed class Registry
{
    private readonly Dictionary<string, IAdapter> _adapters = new();
    private readonly Dictionary<string, (string AdapterId, string UpstreamId)> _external = new();

    public void Register(IAdapter adapter)
    {
        _adapters[adapter.Id] = adapter;
        foreach (var m in adapter.RegisterModels())
            _external[m.ExternalId] = (adapter.Id, m.UpstreamId);
    }

    /// <summary>输入对外 ID，返回 (adapter, upstreamId)；未知返回 null。</summary>
    public (IAdapter Adapter, string UpstreamId)? ResolveModel(string externalId)
    {
        if (!_external.TryGetValue(externalId, out var entry)) return null;
        return (_adapters[entry.AdapterId], entry.UpstreamId);
    }

    /// <summary>对外模型列表：[{ id, object:"model", owned_by }]。</summary>
    public IEnumerable<object> ListModels() =>
        _external.Select(kv => new
        {
            id = kv.Key,
            @object = "model",
            owned_by = kv.Value.AdapterId,
        });

    private sealed class FamilyBucket
    {
        public HashSet<string> Models { get; } = new();
        public Dictionary<string, List<string>> Providers { get; } = new();
    }

    /// <summary>跨平台模型能力矩阵：按模型家族分组，展示各平台提供的具体版本。</summary>
    public IDictionary<string, object> ModelMatrix()
    {
        var families = new SortedDictionary<string, FamilyBucket>(StringComparer.Ordinal);

        foreach (var (adapterId, adapter) in _adapters)
        {
            foreach (var m in adapter.RegisterModels())
            {
                var family = FamilyOf(m.UpstreamId);
                if (!families.TryGetValue(family, out var f))
                    families[family] = f = new FamilyBucket();
                f.Models.Add(m.UpstreamId);
                if (!f.Providers.TryGetValue(adapterId, out var list))
                    f.Providers[adapterId] = list = new List<string>();
                list.Add(m.UpstreamId);
            }
        }

        return families.ToDictionary(
            kv => kv.Key,
            kv => (object)new
            {
                models = kv.Value.Models.ToArray(),
                providers = kv.Value.Providers,
            });
    }

    /// <summary>从模型名推导家族：取连字符首段并转小写（glm-5.2→glm、GLM-5.2→glm、hy3→hy3）。</summary>
    public static string FamilyOf(string upstreamId)
    {
        var idx = upstreamId.IndexOf('-');
        var head = idx > 0 ? upstreamId[..idx] : upstreamId;
        return head.ToLowerInvariant();
    }
}
