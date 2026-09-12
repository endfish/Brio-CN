using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Brio.Resources.Extra;

/// <summary>All known names for an appearance, without changing its BNpcBase identity.</summary>
public sealed class BattleNpcNameIndex
{
    private readonly FrozenDictionary<uint, uint[]> _links;
    private readonly Func<uint, string?> _resolveName;
    private readonly Func<uint, string?> _resolveFallback;
    private readonly ConcurrentDictionary<uint, string> _nameCache = new();
    private readonly ConcurrentDictionary<uint, IReadOnlyList<string>> _baseCache = new();

    public BattleNpcNameIndex(IEnumerable<(uint BaseId, uint NameId)> links,
        Func<uint, string?> resolveName, Func<uint, string?> resolveFallback)
    {
        _links = links.Where(link => link.BaseId != 0 && link.NameId != 0)
            .GroupBy(link => link.BaseId)
            .ToFrozenDictionary(group => group.Key, group => group.Select(link => link.NameId).Distinct().ToArray());
        _resolveName = resolveName;
        _resolveFallback = resolveFallback;
    }

    public string? ResolveNameId(uint nameId)
    {
        var name = _nameCache.GetOrAdd(nameId, id => _resolveName(id)?.Trim() ?? string.Empty);
        return name.Length == 0 ? null : name;
    }

    public IReadOnlyList<string> GetNames(uint baseId) => _baseCache.GetOrAdd(baseId, BuildNames);

    private IReadOnlyList<string> BuildNames(uint baseId)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        void Add(string? name)
        {
            if(!string.IsNullOrWhiteSpace(name) && seen.Add(name.Trim()))
                names.Add(name.Trim());
        }

        if(_links.TryGetValue(baseId, out var nameIds))
            foreach(var nameId in nameIds)
                Add(ResolveNameId(nameId));

        // Overrides also apply to appearances with no supplemental link at all.
        // Keep the existing mapped name first, then add aliases without duplicate rows.
        Add(_resolveFallback(baseId));
        return names.AsReadOnly();
    }
}

public sealed class BattleNpcNameLinkSupplement
{
    public Dictionary<uint, uint[]> Links { get; init; } = [];
}
