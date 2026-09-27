using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Services;

/// <summary>Serializes history writes and Shell commits. Shell failures never affect the connection.</summary>
public sealed class JumpListService
{
    private static readonly IReadOnlySet<string> NoRemovals = new HashSet<string>();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<string>? _history;
    // What the taskbar shows now, so a refresh that changes nothing skips the Shell commit.
    // Null forces the next commit: nothing published yet, or Shell removals made it stale.
    private JumpListEntry[]? _published;

    public async Task RefreshAsync(IEnumerable<ServerEntry> servers, string? connectedId = null)
    {
        // Runs before the first await, so the snapshot is still taken on the caller's (UI)
        // thread; the server collection may change during the disk/Shell awaits.
        var byId = servers.DistinctBy(s => s.Id)
            .ToDictionary(s => s.Id, s => new JumpListEntry(s.Id, s.Name), StringComparer.Ordinal);
        await _gate.WaitAsync();
        try
        {
            _history ??= File.Exists(AppPaths.RecentConnectionsPath)
                ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(AppPaths.RecentConnectionsPath),
                    AppJsonSerializerContext.Default.ListString) ?? []
                : [];
            // A connect always commits: only BeginList reveals that the user removed that node.
            var force = connectedId is not null;
            var removed = await CommitAsync(RecentConnectionHistory.Update(_history, byId.Keys, connectedId), byId, force);
            // Using a previously removed node again is a new explicit action. Re-add it only
            // after the first commit has acknowledged its removal to Windows.
            if (connectedId is not null && removed.Contains(connectedId))
                await CommitAsync(RecentConnectionHistory.Update(_history, byId.Keys, connectedId), byId, force);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[JumpList] Update failed: {ex}");
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlySet<string>> CommitAsync(
        List<string> history, Dictionary<string, JumpListEntry> byId, bool force)
    {
        if (!_history!.SequenceEqual(history)) await SaveAsync(history);
        _history = history;
        var items = history.Select(id => byId[id]).ToArray();
        if (!force && _published is not null && items.SequenceEqual(_published)) return NoRemovals;

        var removed = await JumpListShell.PublishAsync(items, L.JumpList_Recent, PersistShellRemovals);
        _published = removed.Count == 0 ? items : null;
        return removed;
    }

    // Runs on the Shell STA, with _gate held. AtomicFile uses ConfigureAwait(false).
    // The disk write must finish before Shell acknowledges (and forgets) removals.
    private void PersistShellRemovals(IReadOnlySet<string> removed)
    {
        var kept = RecentConnectionHistory.Remove(_history!, removed);
        SaveAsync(kept).GetAwaiter().GetResult();
        _history = kept;
    }

    private static Task SaveAsync(List<string> history) => AtomicFile.WriteAllTextAsync(
        AppPaths.RecentConnectionsPath,
        JsonSerializer.Serialize(history, AppJsonSerializerContext.Readable<List<string>>()));
}
