using System;
using System.Collections.Generic;
using System.Linq;

namespace XrayUI.Services;

public static class RecentConnectionHistory
{
    public const int Capacity = 5;

    public static List<string> Remove(IEnumerable<string> previous, IReadOnlySet<string> removed) =>
        previous.Where(id => !removed.Contains(id)).ToList();

    public static List<string> Update(IEnumerable<string> previous, IEnumerable<string> availableIds, string? connectedId = null)
    {
        var available = availableIds.ToHashSet(StringComparer.Ordinal);
        var candidates = connectedId is null ? previous : previous.Prepend(connectedId);
        return candidates.Where(id => !string.IsNullOrWhiteSpace(id) && available.Contains(id))
            .Distinct(StringComparer.Ordinal).Take(Capacity).ToList();
    }
}
