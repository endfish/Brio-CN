using Brio.Resources;
using Dalamud.Plugin;
using Lumina.Excel.Sheets;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Brio.IPC;

public sealed class PenumbraModActionService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly PenumbraService _penumbraService;
    private readonly GetEnabledState _getEnabledState;
    private readonly GetCollectionForObject _getCollectionForObject;
    private readonly GetCollection _getCollection;
    private readonly GetChangedItemsForCollection _getChangedItemsForCollection;
    private readonly CheckCurrentChangedItemFunc _checkCurrentChangedItem;
    private readonly GetModList _getModList;
    private readonly ResolveGameObjectPath _resolveGameObjectPath;

    private IReadOnlyList<PenumbraModAction> _cachedActions = [];
    private string _statusMessageSource = "Penumbra mod actions have not been scanned yet.";
    private object?[] _statusMessageArguments = [];
    private DateTime _nextRefreshAtUtc = DateTime.MinValue;
    private ushort _cachedObjectIndex = ushort.MaxValue;
    private ActionTimelineContext? _cachedContext;

    public int Version { get; private set; }
    public string StatusMessage => Localize.Format(_statusMessageSource, _statusMessageArguments);

    public PenumbraModActionService(IDalamudPluginInterface pluginInterface, PenumbraService penumbraService)
    {
        _penumbraService = penumbraService;
        _getEnabledState = new GetEnabledState(pluginInterface);
        _getCollectionForObject = new GetCollectionForObject(pluginInterface);
        _getCollection = new GetCollection(pluginInterface);
        _getChangedItemsForCollection = new GetChangedItemsForCollection(pluginInterface);
        _checkCurrentChangedItem = new CheckCurrentChangedItemFunc(pluginInterface);
        _getModList = new GetModList(pluginInterface);
        _resolveGameObjectPath = new ResolveGameObjectPath(pluginInterface);
    }

    public IReadOnlyList<PenumbraModAction> GetActiveActions(ushort objectIndex, ActionTimelineContext? actorContext)
    {
        if(_cachedObjectIndex == objectIndex && _cachedContext == actorContext && DateTime.UtcNow < _nextRefreshAtUtc)
            return _cachedActions;

        _cachedObjectIndex = objectIndex;
        _cachedContext = actorContext;
        _nextRefreshAtUtc = DateTime.UtcNow + RefreshInterval;

        Refresh(objectIndex, actorContext);
        return _cachedActions;
    }

    private void Refresh(ushort objectIndex, ActionTimelineContext? actorContext)
    {
        try
        {
            if(!_penumbraService.AllowIntegration || !_penumbraService.IsAvailable)
            {
                SetCache([], "Penumbra integration is unavailable or disabled.");
                return;
            }

            if(!_getEnabledState.Invoke())
            {
                SetCache([], "Penumbra is currently disabled.");
                return;
            }

            var (objectValid, _, collection) = _getCollectionForObject.Invoke(objectIndex);
            if(!objectValid)
            {
                SetCache([], "The actor's Penumbra collection could not be resolved.");
                return;
            }

            var changedItems = _getChangedItemsForCollection.Invoke(collection.Id);
            var actions = BuildActions(changedItems, collection.Id, objectIndex, actorContext);
            if(actions.Count == 0)
                SetCache(actions, "No active mod actions were found in {0}.", collection.Name);
            else
                SetCache(actions, "{0} active mod actions from {1}.", actions.Count, collection.Name);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "Failed to scan Penumbra mod actions");
            SetCache([], "Failed to scan Penumbra mod actions.");
        }
    }

    private IReadOnlyList<PenumbraModAction> BuildActions(
        IReadOnlyDictionary<string, object?> changedItems,
        Guid collectionId,
        ushort objectIndex,
        ActionTimelineContext? actorContext)
    {
        var hits = new List<PoseRedirect>();
        try
        {
            hits.AddRange(FindPoseRedirects(actorContext,
                id => GameDataProvider.Instance.ActionTimelines.TryGetRow(id, out var timeline)
                    ? (timeline.Key.ToString(), timeline.LoadType)
                    : null,
                Brio.GameFileExists, path => _resolveGameObjectPath.Invoke(path, objectIndex)));
        }
        catch(Exception ex)
        {
            // Exact resolution is optional; ordinary changed-emote entries are
            // still useful if a Penumbra build does not expose this endpoint.
            Brio.Log.Debug(ex, "Failed to resolve Penumbra common pose paths");
        }

        var actions = new List<PenumbraModAction>();
        var detected = new HashSet<(string Directory, uint EmoteId)>();
        var modDirectories = hits.Select(hit => GetModDirectory(_penumbraService.ModDirectory, hit.ResolvedPath)).ToArray();
        var modNames = modDirectories.Any(directory => directory is not null) ? TryGetModNames() : null;
        for(var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var directory = modDirectories[i];
            var displayName = directory is not null ? modNames?.GetValueOrDefault(directory) : null;
            var modName = string.IsNullOrWhiteSpace(displayName) ? directory : displayName;
            actions.Add(new(null, string.Empty, modName, hit.Pose));

            if(directory is not null && hit.Pose.BaseEmoteId != 0)
                detected.Add((directory.ToUpperInvariant(), hit.Pose.BaseEmoteId));
        }

        // This Penumbra endpoint is tied to its UI-selected collection, not
        // the actor's. Never borrow a mod name from a different collection.
        var modLookup = GetChangedItemLookup(collectionId);
        foreach(var (changedItemName, payload) in changedItems.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if(payload is not Emote emote || emote.RowId is 0 or > ushort.MaxValue)
                continue;

            var pairs = modLookup?.Invoke(changedItemName) ?? [];
            var names = pairs.Where(mod => !detected.Contains((mod.ModDirectory.ToUpperInvariant(), emote.RowId)))
                .Select(mod => string.IsNullOrWhiteSpace(mod.ModName) ? mod.ModDirectory : mod.ModName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if(pairs.Length > 0 && pairs.All(mod => detected.Contains((mod.ModDirectory.ToUpperInvariant(), emote.RowId))))
                continue;

            actions.Add(new(emote, emote.Name.ToString().Trim(), BuildModLabel(names), null));
        }

        return actions;
    }

    private Func<string, (string ModDirectory, string ModName)[]>? GetChangedItemLookup(Guid collectionId)
    {
        try
        {
            return _getCollection.Invoke(ApiCollectionType.Current)?.Id == collectionId
                ? _checkCurrentChangedItem.Invoke()
                : null;
        }
        catch(Exception ex)
        {
            Brio.Log.Debug(ex, "Failed to get Penumbra changed-emote source names");
            return null;
        }
    }

    private Dictionary<string, string>? TryGetModNames()
    {
        try
        {
            return new(_getModList.Invoke(), StringComparer.OrdinalIgnoreCase);
        }
        catch(Exception ex)
        {
            Brio.Log.Debug(ex, "Failed to get Penumbra mod display names");
            return null;
        }
    }

    internal static IEnumerable<PoseRedirect> FindPoseRedirects(
        ActionTimelineContext? context,
        Func<uint, (string Key, byte LoadType)?> getTimeline,
        Func<string, bool> gameFileExists,
        Func<string, string> resolvePath)
    {
        if(context is not { ModelKind: ActionTimelineModelKind.Human } human)
            yield break;

        var root = $"chara/human/{human.ModelCode}/animation/{human.AnimationVariantCode}/bt_common/";
        foreach(var pose in CommonPoseCatalog.All)
        {
            // A shared resident pack may change other clips without changing
            // this pose. Only claim an exact number for standalone PAP files.
            if(getTimeline(pose.TimelineId) is not { LoadType: 1 } timeline
                || string.IsNullOrWhiteSpace(timeline.Key))
                continue;

            var gamePath = $"{root}{timeline.Key}.pap";
            if(!gameFileExists(gamePath))
                continue;

            var resolved = resolvePath(gamePath);
            if(!string.IsNullOrWhiteSpace(resolved) && !string.Equals(resolved.Replace('\\', '/'), gamePath, StringComparison.OrdinalIgnoreCase))
                yield return new(pose, resolved);
        }
    }

    internal static string? GetModDirectory(string? root, string resolvedPath)
    {
        if(string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(resolvedPath))
            return null;

        try
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(resolvedPath);
            if(!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            var relative = path.AsSpan(prefix.Length);
            var separator = relative.IndexOf(Path.DirectorySeparatorChar);
            return separator > 0 ? relative[..separator].ToString() : null;
        }
        catch(ArgumentException)
        {
            return null;
        }
    }

    private static string? BuildModLabel(IReadOnlyList<string> names)
        => names.Count switch
        {
            0 => null,
            1 => names[0],
            2 => $"{names[0]} / {names[1]}",
            _ => $"{names[0]} +{names.Count - 1}",
        };

    private void SetCache(
        IReadOnlyList<PenumbraModAction> actions,
        string statusSource,
        params object?[] statusArguments)
    {
        if(!_cachedActions.SequenceEqual(actions))
        {
            _cachedActions = actions;
            Version++;
        }

        _statusMessageSource = statusSource;
        _statusMessageArguments = statusArguments;
    }

    internal readonly record struct PoseRedirect(CommonPoseDefinition Pose, string ResolvedPath);
}

public sealed record PenumbraModAction(Emote? Emote, string EmoteName, string? ModName, CommonPoseDefinition? Pose)
{
    public uint? TimelineId => Pose?.TimelineId;
    public string DisplayModName => ModName ?? Localize.Text("Penumbra (mod unconfirmed)");
    public string DisplayActionName
    {
        get
        {
            if(Pose is { } pose)
                return pose.DisplayName;

            var name = string.IsNullOrWhiteSpace(EmoteName) ? Localize.Format("Emote {0}", Emote?.RowId ?? 0) : EmoteName;
            return CommonPoseCatalog.IsBaseEmote(Emote?.RowId ?? 0)
                ? Localize.Format("{0} (pose number unconfirmed)", name)
                : name;
        }
    }
}
