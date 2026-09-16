namespace ProxyHub;

/// <summary>
/// 适配器注册表：按模型对外 ID 路由到适配器，维护 对外ID ↔ 上游ID 映射。
/// 模型来源分级：动态（运行时从平台拉取当前可用模型）优先，静态基线兜底。
/// </summary>
public sealed class Registry
{
    private readonly Dictionary<string, IAdapter> _adapters = new();
    private readonly Dictionary<string, (string AdapterId, string UpstreamId)> _external = new();
    private readonly Dictionary<string, IReadOnlyList<ModelRegistration>> _dynamic = new();

    public void Register(IAdapter adapter)
    {
        _adapters[adapter.Id] = adapter;
        foreach (var m in adapter.RegisterModels())
            _external[m.ExternalId] = (adapter.Id, m.UpstreamId);
    }

    /// <summary>
    /// 尝试对所有适配器动态拉取模型列表；成功且非空的适配器用动态映射覆盖静态基线，
    /// 不支持或失败的适配器保留其既有（动态/静态）映射。并发安全。
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var tasks = _adapters.Values.Select(async adapter =>
        {
            try
            {
                var dyn = await adapter.FetchModelsAsync(ct).ConfigureAwait(false);
                if (dyn is { Count: > 0 })
                    _dynamic[adapter.Id] = dyn;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 拉取失败静默回退：保留该适配器已有映射
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>该适配器当前生效的模型：动态优先，无动态则静态基线。</summary>
    private IReadOnlyList<ModelRegistration> EffectiveModels(IAdapter adapter) =>
        _dynamic.TryGetValue(adapter.Id, out var dyn) ? dyn : adapter.RegisterModels();

    /// <summary>某适配器当前生效的模型数量（可配置时从动态表读取，无需私有字段）。</summary>
    public int EffectiveCount(string adapterId) =>
        _dynamic.TryGetValue(adapterId, out var dyn) ? dyn.Count : _external.Count(kv => kv.Value.AdapterId == adapterId);

    /// <summary>输入对外 ID，返回 (adapter, upstreamId)；未知返回 null。动态映射优先（含静态表没有的新增模型）。</summary>
    public (IAdapter Adapter, string UpstreamId)? ResolveModel(string externalId)
    {
        foreach (var (adapterId, dyn) in _dynamic)
        {
            var dm = dyn.FirstOrDefault(mm => mm.ExternalId == externalId);
            if (dm is not null) return (_adapters[adapterId], dm.UpstreamId);
        }
        if (_external.TryGetValue(externalId, out var entry))
            return (_adapters[entry.AdapterId], entry.UpstreamId);

        // 后备匹配：支持带或不带适配器前缀
        foreach (var (adapterId, adapter) in _adapters)
        {
            if (externalId.StartsWith($"{adapterId}-", StringComparison.OrdinalIgnoreCase))
            {
                var bare = externalId[(adapterId.Length + 1)..];
                var m = EffectiveModels(adapter).FirstOrDefault(x => x.UpstreamId.Equals(bare, StringComparison.OrdinalIgnoreCase));
                if (m is not null) return (adapter, m.UpstreamId);
            }
            else
            {
                var m = EffectiveModels(adapter).FirstOrDefault(x => x.UpstreamId.Equals(externalId, StringComparison.OrdinalIgnoreCase));
                if (m is not null) return (adapter, m.UpstreamId);
            }
        }
        return null;
    }

    /// <summary>对外模型列表：[{ id, object:"model", owned_by }]，仅含当前生效模型。</summary>
    public IEnumerable<object> ListModels() =>
        _adapters.SelectMany(kv => EffectiveModels(kv.Value).Select(m => (AdapterId: kv.Key, Model: m)))
            .Select(x => new
            {
                id = x.Model.ExternalId,
                @object = "model",
                owned_by = x.AdapterId,
            });

    private sealed class FamilyBucket
    {
        public HashSet<string> Models { get; } = new();
        public Dictionary<string, List<string>> Providers { get; } = new();
    }

    /// <summary>跨平台模型能力矩阵：按模型家族分组，展示各平台提供的具体版本（仅当前生效模型）。</summary>
    public IDictionary<string, object> ModelMatrix()
    {
        var families = new SortedDictionary<string, FamilyBucket>(StringComparer.Ordinal);

        foreach (var (adapterId, adapter) in _adapters)
        {
            foreach (var m in EffectiveModels(adapter))
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