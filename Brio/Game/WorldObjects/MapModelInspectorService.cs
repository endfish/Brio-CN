using Brio.Resources;
using Brio.Resources.Extra;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CSBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;
using CSDrawObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using CSObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;
using CSSphereBounds = FFXIVClientStructs.FFXIV.Common.Math.SphereBounds;

namespace Brio.Game.WorldObjects;

public sealed record MapModelInstance(
    string Path,
    nint Address,
    uint InstanceKey,
    uint SubId,
    Vector3 Position,
    Vector3 BoundsCenter,
    float BoundsRadius,
    bool IsActive,
    bool IsLoaded);

public sealed record MapModelEntry(
    string Path,
    string ModelCode,
    string DisplayName,
    GamePathInfo PathInfo,
    IReadOnlyList<MapModelInstance> Instances);

public sealed record MapModelScanResult(
    uint TerritoryType,
    IReadOnlyList<MapModelEntry> Entries,
    IReadOnlyList<MapModelInstance> Instances)
{
    public static MapModelScanResult Empty { get; } = new(0, [], []);
}

public sealed record MapModelPickDiagnostics(
    Vector2 ScreenPosition,
    int SphereCandidates,
    int ScreenCandidates,
    int TotalCandidates,
    string Status)
{
    public static MapModelPickDiagnostics Empty { get; } = new(
        Vector2.Zero,
        0,
        0,
        0,
        "No pick has been attempted.");
}

public sealed class MapModelInspectorService
{
    private const float DefaultPickRadius = 1.5f;
    private const float MaximumSanePickRadius = 100_000f;

    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ITargetManager _targetManager;

    private static readonly InstanceType[] ColliderOwnerTypes =
    [
        InstanceType.BgPart,
        InstanceType.SharedGroup,
        InstanceType.CollisionBox,
        InstanceType.SphereCastRange,
        InstanceType.IndoorObject,
        InstanceType.OutdoorObject,
        InstanceType.ColliderLayer7,
        InstanceType.ColliderLayer8,
        InstanceType.ColliderLayer9,
        InstanceType.ColliderLayer10,
    ];

    public MapModelScanResult Snapshot { get; private set; } = MapModelScanResult.Empty;
    public MapModelPickDiagnostics LastPickDiagnostics { get; private set; } = MapModelPickDiagnostics.Empty;

    public MapModelInspectorService(
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager)
    {
        _framework = framework;
        _clientState = clientState;
        _objectTable = objectTable;
        _targetManager = targetManager;
    }

    public async Task<MapModelScanResult> ScanAsync()
        => await _framework.RunOnFrameworkThread(ScanInternal);

    public void UseSnapshot(MapModelScanResult snapshot)
    {
        Snapshot = snapshot;
        LastPickDiagnostics = MapModelPickDiagnostics.Empty;
    }

    public void Clear()
    {
        Snapshot = MapModelScanResult.Empty;
        LastPickDiagnostics = MapModelPickDiagnostics.Empty;
    }

    public async Task<IReadOnlyList<MapModelInstance>> PickAsync(
        Vector2 screenPosition,
        string preferredAssetType,
        bool strictAssetType)
    {
        var snapshot = Snapshot;
        if(snapshot.Entries.Count == 0 || snapshot.TerritoryType != _clientState.TerritoryType)
        {
            LastPickDiagnostics = MapModelPickDiagnostics.Empty with
            {
                ScreenPosition = screenPosition,
                Status = "The snapshot is empty or belongs to another territory.",
            };
            return [];
        }

        var result = await _framework.RunOnFrameworkThread(
            () => PickInternal(screenPosition, snapshot, preferredAssetType, strictAssetType));
        LastPickDiagnostics = result.Diagnostics;
        return result.Candidates;
    }

    private unsafe MapModelScanResult ScanInternal()
    {
        var territoryType = _clientState.TerritoryType;
        var playerPosition = _objectTable.LocalPlayer?.Position;
        var world = LayoutWorld.Instance();
        if(world == null)
            return new MapModelScanResult(territoryType, [], []);

        var instances = new List<MapModelInstance>();
        var visitedLayouts = new HashSet<nint>();
        var visitedInstances = new HashSet<nint>();

        CollectLayout(world->ActiveLayout);
        CollectLayout(world->GlobalLayout);
        CollectTargetableGameObjects();

        var entries = instances
            .GroupBy(instance => instance.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var path = group.Key;
                var info = PathIndex.ParsePath(path);
                var metadata = GameDataProvider.Instance.PathDatabase.GetPathDataByPath(path);
                var displayName = string.IsNullOrWhiteSpace(metadata?.Name) ? info.DisplayName : metadata.Name;
                if(metadata is not null)
                {
                    info = info with
                    {
                        Expansion = string.IsNullOrWhiteSpace(metadata.Expansion) ? info.Expansion : metadata.Expansion,
                        Subtype = metadata.Subtypes.FirstOrDefault() ?? info.Subtype,
                        AssetType = metadata.AssetType.FirstOrDefault() ?? info.AssetType,
                    };
                }

                return new MapModelEntry(
                    path,
                    PathData.FileName(path),
                    displayName,
                    info,
                    group
                        .OrderBy(instance => playerPosition is null
                            ? 0
                            : Vector3.DistanceSquared(instance.Position, playerPosition.Value))
                        .ThenBy(instance => instance.InstanceKey)
                        .ThenBy(instance => instance.SubId)
                        .ToArray());
            })
            .OrderBy(entry => entry.PathInfo.AssetType)
            .ThenBy(entry => entry.DisplayName)
            .ThenBy(entry => entry.Path)
            .ToArray();

        Brio.Log.Info($"Map model inspector scanned territory {territoryType}: {entries.Length} model paths, {instances.Count} instances");
        return new MapModelScanResult(territoryType, entries, instances.ToArray());

        void CollectLayout(LayoutManager* layout)
        {
            if(layout == null || layout->InitState < 7 || !visitedLayouts.Add((nint)layout))
                return;

            if(!layout->InstancesByType.TryGetValue(InstanceType.BgPart, out var instanceMapPointer, false)
                || instanceMapPointer.Value == null)
                return;

            foreach(var item in *instanceMapPointer.Value)
            {
                var instance = item.Item2.Value;
                TryAddLayoutInstance(instance);
            }
        }

        void CollectTargetableGameObjects()
        {
            foreach(var gameObject in _objectTable)
            {
                if(gameObject is null || gameObject.Address == IntPtr.Zero)
                    continue;

                var native = (CSGameObject*)gameObject.Address;
                if(native->SharedGroupLayoutInstance != null)
                    CollectSharedGroup(native->SharedGroupLayoutInstance);

                TryAddDrawObject(gameObject, native->DrawObject);
            }
        }

        void CollectSharedGroup(SharedGroupLayoutInstance* group)
        {
            if(group == null)
                return;

            foreach(var childPointer in group->Instances.Instances)
            {
                var child = childPointer.Value;
                if(child == null || child->Instance == null)
                    continue;

                var instance = child->Instance;
                if(instance->Id.Type == InstanceType.SharedGroup)
                    CollectSharedGroup((SharedGroupLayoutInstance*)instance);
                else
                    TryAddLayoutInstance(instance);
            }
        }

        void TryAddLayoutInstance(ILayoutInstance* instance)
        {
            if(instance == null || !visitedInstances.Add((nint)instance))
                return;

            var primaryPath = instance->GetPrimaryPath();
            if(!primaryPath.HasValue)
                return;

            var path = PathData.Normalize(primaryPath.ToString());
            if(!path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                return;

            var transform = instance->GetTransformImpl();
            var position = transform == null ? Vector3.Zero : transform->Translation;
            var boundsCenter = position;
            var boundsRadius = DefaultPickRadius;
            var graphics = instance->GetGraphics();
            var isLoaded = graphics != null;

            if(isLoaded)
            {
                var sphere = Vector4.Zero;
                var result = instance->GetBoundingSphereImpl(&sphere);
                var radius = MathF.Abs(sphere.W);

                if(result != null && IsSaneSphere(sphere.X, sphere.Y, sphere.Z, radius))
                {
                    boundsCenter = new Vector3(sphere.X, sphere.Y, sphere.Z);
                    boundsRadius = radius;
                }
            }

            instances.Add(new MapModelInstance(
                path,
                (nint)instance,
                instance->Id.InstanceKey,
                instance->SubId,
                position,
                boundsCenter,
                boundsRadius,
                instance->IsActive,
                isLoaded));
        }

        void TryAddDrawObject(IGameObject gameObject, CSDrawObject* drawObject)
        {
            if(drawObject == null
                || drawObject->Object.GetObjectType() != CSObjectType.BgObject
                || !visitedInstances.Add((nint)drawObject))
                return;

            var bgObject = (CSBgObject*)drawObject;
            var resource = bgObject->ModelResourceHandle;
            if(resource == null)
                return;

            var path = PathData.Normalize(resource->ResourceHandle.FileName.ToString());
            if(!path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                return;

            var position = gameObject.Position;
            var boundsCenter = position;
            var boundsRadius = MathF.Max(gameObject.HitboxRadius, DefaultPickRadius);
            var isLoaded = drawObject->LoadState == 3 && resource->ResourceHandle.LoadState == 7;

            if(isLoaded)
            {
                var sphere = default(CSSphereBounds);
                if(drawObject->ComputeSphereBounds(&sphere) != null
                    && IsSaneSphere(sphere.CenterPoint.X, sphere.CenterPoint.Y, sphere.CenterPoint.Z, MathF.Abs(sphere.Radius)))
                {
                    boundsCenter = new Vector3(sphere.CenterPoint.X, sphere.CenterPoint.Y, sphere.CenterPoint.Z);
                    boundsRadius = MathF.Abs(sphere.Radius);
                }
            }

            instances.Add(new MapModelInstance(
                path,
                (nint)drawObject,
                (uint)(gameObject.GameObjectId & uint.MaxValue),
                gameObject.ObjectIndex,
                position,
                boundsCenter,
                boundsRadius,
                true,
                isLoaded));
        }
    }

    private unsafe (IReadOnlyList<MapModelInstance> Candidates, MapModelPickDiagnostics Diagnostics) PickInternal(
        Vector2 screenPosition,
        MapModelScanResult snapshot,
        string preferredAssetType,
        bool strictAssetType)
    {
        var instances = snapshot.Instances;
        var control = Control.Instance();
        if(control == null)
            return ([], MapModelPickDiagnostics.Empty with
            {
                ScreenPosition = screenPosition,
                Status = "Control.Instance() is null.",
            });

        var camera = control->CameraManager.GetActiveCamera();
        if(camera == null)
            return ([], MapModelPickDiagnostics.Empty with
            {
                ScreenPosition = screenPosition,
                Status = "The active game camera is null.",
            });

        var ray = camera->SceneCamera.ScreenPointToRay(screenPosition);
        Vector3 origin = new(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
        Vector3 direction = new(ray.Direction.X, ray.Direction.Y, ray.Direction.Z);
        if(Vector3.Dot(direction, direction) < float.Epsilon)
            return ([], MapModelPickDiagnostics.Empty with
            {
                ScreenPosition = screenPosition,
                Status = "The screen ray direction is empty.",
            });

        direction = Vector3.Normalize(direction);

        var mouseover = ResolveMouseoverContext();
        var assetTypes = snapshot.Entries.ToDictionary(
            entry => entry.Path,
            entry => entry.PathInfo.AssetType,
            StringComparer.OrdinalIgnoreCase);
        var exactTargetAddresses = new HashSet<nint>(mouseover.Addresses);

        if(exactTargetAddresses.Count == 0 && mouseover.Paths.Count > 0 && mouseover.Position is Vector3 targetPosition)
        {
            var nearestPathMatch = instances
                .Where(instance => mouseover.Paths.Contains(instance.Path))
                .OrderBy(instance => Vector3.DistanceSquared(instance.BoundsCenter, targetPosition))
                .FirstOrDefault();
            if(nearestPathMatch is not null)
                exactTargetAddresses.Add(nearestPathMatch.Address);
        }

        bool IsEligible(MapModelInstance instance)
        {
            if(string.IsNullOrEmpty(preferredAssetType) || !strictAssetType)
                return true;

            return assetTypes.TryGetValue(instance.Path, out var assetType)
                && assetType.Equals(preferredAssetType, StringComparison.OrdinalIgnoreCase);
        }

        var scoredCandidates = instances
            .Where(IsEligible)
            .Select(instance =>
            {
                var distance = RaySphereDistance(
                    origin,
                    direction,
                    instance.BoundsCenter,
                    instance.BoundsRadius);
                var exactTarget = exactTargetAddresses.Contains(instance.Address);
                if(distance is null && !exactTarget)
                    return (Instance: instance, Score: float.MaxValue, RayHit: false, ExactTarget: false);

                var toCenter = instance.BoundsCenter - origin;
                var alongRay = MathF.Max(0, Vector3.Dot(toCenter, direction));
                var perpendicular = MathF.Sqrt(MathF.Max(
                    0,
                    toCenter.LengthSquared() - (alongRay * alongRay)));
                var normalizedOffset = perpendicular / MathF.Max(instance.BoundsRadius, 0.25f);

                var nativeCenter = new FFXIVClientStructs.FFXIV.Common.Math.Vector3(
                    instance.BoundsCenter.X,
                    instance.BoundsCenter.Y,
                    instance.BoundsCenter.Z);
                var nativeProjected = default(FFXIVClientStructs.FFXIV.Common.Math.Vector2);
                var visible = camera->SceneCamera.WorldToScreen(nativeCenter, out nativeProjected);
                var projected = new Vector2(nativeProjected.X, nativeProjected.Y);
                var screenDistance = visible
                    ? Vector2.Distance(projected, screenPosition)
                    : 2_000f;

                assetTypes.TryGetValue(instance.Path, out var assetType);
                var preferred = !string.IsNullOrEmpty(preferredAssetType)
                    && preferredAssetType.Equals(assetType, StringComparison.OrdinalIgnoreCase);
                var targetDistance = mouseover.Position is Vector3 position
                    ? MathF.Max(0, Vector3.Distance(instance.BoundsCenter, position) - instance.BoundsRadius)
                    : 0;

                var score =
                    (normalizedOffset * 80f)
                    + (screenDistance * 0.08f)
                    + (MathF.Min(instance.BoundsRadius, 100f) * 2.5f)
                    + (MathF.Max(0, distance ?? alongRay) * 0.01f)
                    + (MathF.Min(targetDistance, 100f) * 2f);

                if(IsStructuralModel(assetType, instance.Path))
                    score += 80f;
                if(preferred)
                    score -= 250f;
                if(exactTarget)
                    score -= 100_000f;

                return (Instance: instance, Score: score, RayHit: distance is not null, ExactTarget: exactTarget);
            })
            .Where(candidate => candidate.Score < float.MaxValue)
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Instance.BoundsRadius)
            .ThenBy(candidate => candidate.Instance.Address)
            .ToList();

        var sphereCandidates = scoredCandidates.Count(candidate => candidate.RayHit);
        var mouseoverCandidates = scoredCandidates.Count(candidate => candidate.ExactTarget);
        var candidates = scoredCandidates
            .Select(candidate => candidate.Instance)
            .Take(32)
            .ToList();

        var collisionMatch = ResolveCollisionMatch(
            origin,
            direction,
            instances);
        if(collisionMatch is not null && mouseoverCandidates == 0 && IsEligible(collisionMatch))
        {
            candidates.RemoveAll(candidate => candidate.Address == collisionMatch.Address);
            candidates.Insert(0, collisionMatch);
        }

        var screenCandidates = 0;
        if(candidates.Count == 0)
        {
            var screenMatches = instances
                .Where(IsEligible)
                .Select(instance =>
                {
                    var nativeCenter = new FFXIVClientStructs.FFXIV.Common.Math.Vector3(
                        instance.BoundsCenter.X,
                        instance.BoundsCenter.Y,
                        instance.BoundsCenter.Z);
                    var nativeProjected = default(FFXIVClientStructs.FFXIV.Common.Math.Vector2);
                    var visible = camera->SceneCamera.WorldToScreen(nativeCenter, out nativeProjected);
                    var projected = new Vector2(nativeProjected.X, nativeProjected.Y);
                    return (Instance: instance, Distance: visible ? Vector2.Distance(projected, screenPosition) : float.MaxValue);
                })
                .Where(candidate => candidate.Distance <= 36f)
                .OrderBy(candidate => candidate.Distance)
                .Select(candidate => candidate.Instance)
                .Take(8)
                .ToArray();
            screenCandidates = screenMatches.Length;
            candidates.AddRange(screenMatches);
        }

        var diagnostics = new MapModelPickDiagnostics(
            screenPosition,
            sphereCandidates,
            screenCandidates,
            candidates.Count,
            mouseoverCandidates > 0
                ? "The game mouseover target was matched."
                : candidates.Count > 0
                    ? "Candidates found."
                    : "The ray was valid, but no scanned model matched it.");

        return (candidates, diagnostics);
    }

    private unsafe MouseoverContext ResolveMouseoverContext()
    {
        var target = _targetManager.MouseOverTarget ?? _targetManager.MouseOverNameplateTarget;
        if(target is null || target.Address == IntPtr.Zero)
            return MouseoverContext.Empty;

        var native = (CSGameObject*)target.Address;
        var addresses = new HashSet<nint>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if(native->DrawObject != null)
        {
            addresses.Add((nint)native->DrawObject);
            if(native->DrawObject->Object.GetObjectType() == CSObjectType.BgObject)
            {
                var bgObject = (CSBgObject*)native->DrawObject;
                var resource = bgObject->ModelResourceHandle;
                if(resource != null)
                {
                    var path = PathData.Normalize(resource->ResourceHandle.FileName.ToString());
                    if(path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                        paths.Add(path);
                }
            }
        }

        CollectSharedGroupTargets(native->SharedGroupLayoutInstance, addresses, paths);

        return new MouseoverContext(
            target.Position,
            addresses,
            paths);
    }

    private static unsafe void CollectSharedGroupTargets(
        SharedGroupLayoutInstance* group,
        HashSet<nint> addresses,
        HashSet<string> paths)
    {
        if(group == null)
            return;

        foreach(var childPointer in group->Instances.Instances)
        {
            var child = childPointer.Value;
            if(child == null || child->Instance == null)
                continue;

            var instance = child->Instance;
            if(instance->Id.Type == InstanceType.SharedGroup)
            {
                CollectSharedGroupTargets((SharedGroupLayoutInstance*)instance, addresses, paths);
                continue;
            }

            var primaryPath = instance->GetPrimaryPath();
            if(!primaryPath.HasValue)
                continue;

            var path = PathData.Normalize(primaryPath.ToString());
            if(!path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                continue;

            addresses.Add((nint)instance);
            paths.Add(path);
        }
    }

    private static bool IsStructuralModel(string? assetType, string path)
    {
        var value = $"{assetType} {path}".ToLowerInvariant();
        return value.Contains("wall", StringComparison.Ordinal)
            || value.Contains("floor", StringComparison.Ordinal)
            || value.Contains("roof", StringComparison.Ordinal)
            || value.Contains("ceiling", StringComparison.Ordinal)
            || value.Contains("terrain", StringComparison.Ordinal)
            || value.Contains("cliff", StringComparison.Ordinal)
            || value.Contains("墙", StringComparison.Ordinal)
            || value.Contains("地板", StringComparison.Ordinal)
            || value.Contains("地面", StringComparison.Ordinal)
            || value.Contains("屋顶", StringComparison.Ordinal)
            || value.Contains("天花", StringComparison.Ordinal)
            || value.Contains("地形", StringComparison.Ordinal);
    }

    private static unsafe MapModelInstance? ResolveCollisionMatch(
        Vector3 origin,
        Vector3 direction,
        IReadOnlyList<MapModelInstance> instances)
    {
        if(!BGCollisionModule.RaycastMaterialFilter(origin, direction, out var hit)
            || hit.Object == null)
            return null;

        foreach(var type in ColliderOwnerTypes)
        {
            var owner = LayoutWorld.GetColliderLayoutInstance(type, hit.Object);
            if(owner == null)
                continue;

            var exact = instances.FirstOrDefault(instance => instance.Address == (nint)owner);
            if(exact is not null)
                return exact;

            var primaryPath = owner->GetPrimaryPath();
            if(primaryPath.HasValue)
            {
                var path = PathData.Normalize(primaryPath.ToString());
                var hitPoint = new Vector3(hit.Point.X, hit.Point.Y, hit.Point.Z);
                var samePath = instances
                    .Where(instance => instance.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(instance => Vector3.DistanceSquared(instance.BoundsCenter, hitPoint))
                    .FirstOrDefault();
                if(samePath is not null)
                    return samePath;
            }
        }

        return null;
    }

    private static float? RaySphereDistance(Vector3 origin, Vector3 direction, Vector3 center, float radius)
    {
        var toCenter = center - origin;
        var projected = Vector3.Dot(toCenter, direction);
        var perpendicularSquared = toCenter.LengthSquared() - (projected * projected);
        var radiusSquared = radius * radius;
        if(perpendicularSquared > radiusSquared)
            return null;

        var halfChord = MathF.Sqrt(MathF.Max(0, radiusSquared - perpendicularSquared));
        var near = projected - halfChord;
        var far = projected + halfChord;
        if(far < 0)
            return null;

        return MathF.Max(0, near);
    }

    private static bool IsSaneSphere(float x, float y, float z, float radius)
        => IsFinite(x)
        && IsFinite(y)
        && IsFinite(z)
        && IsFinite(radius)
        && radius > 0.001f
        && radius <= MaximumSanePickRadius;

    private static bool IsFinite(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value);

    private sealed record MouseoverContext(
        Vector3? Position,
        HashSet<nint> Addresses,
        HashSet<string> Paths)
    {
        public static MouseoverContext Empty { get; } = new(
            null,
            [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}
