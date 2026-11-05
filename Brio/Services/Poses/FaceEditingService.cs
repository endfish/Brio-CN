using Brio.Capabilities.Actor;
using Brio.Capabilities.Posing;
using Brio.Core;
using Brio.Entities;
using Brio.Game.GPose;
using Brio.Game.Posing;
using Brio.Game.Posing.Skeletons;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Brio.Services.Poses;

public sealed class FaceEditingService : IDisposable
{
    private readonly EntityManager _entities;
    private readonly GPoseService _gpose;
    private readonly SkeletonService _skeletons;
    private readonly HistoryService _history;
    private PosingCapability? _capability;
    private Skeleton? _skeleton;
    private PoseInfo? _poseInfo;
    private Bone? _head;
    private long _revision;
    private bool _dirty, _commit, _endAfterCommit;
    public FacePoseSolver? Rig { get; private set; }
    public FaceControlValues Values { get; private set; } = new();
    public bool IsBound => IsValid();

    public FaceEditingService(EntityManager entities, GPoseService gpose, SkeletonService skeletons, HistoryService history)
    {
        _entities = entities; _gpose = gpose; _skeletons = skeletons; _history = history;
        skeletons.SkeletonUpdateEnd += OnUpdateEnd;
        gpose.OnGPoseStateChange += OnGPose;
    }

    public bool Capture()
    {
        End();
        if(!_gpose.IsGPosing || !_entities.TryGetCapabilityFromSelectedEntity<PosingCapability>(out var cap, considerParents: false)
            || cap.SkeletonPosing.CharacterSkeleton is not { IsValid: true } skeleton || cap.SkeletonPosing.HasTransitiveActions)
            return false;
        var head = skeleton.Bones.FirstOrDefault(b => b.Name == "j_kao");
        if(head is null) return false;
        var bones = skeleton.Bones.Where(b => b.IsFaceBone && b.Name.StartsWith("j_f_", StringComparison.Ordinal))
            .Select(b => new FaceRigBone(new(b.Name, b.PartialId, PoseInfoSlot.Character),
                b.Parent is { } parent ? new BonePoseInfoId(parent.Name, parent.PartialId, PoseInfoSlot.Character) : null, b.LastRawTransform)).ToArray();
        try { Rig = new FacePoseSolver(head.LastRawTransform, bones); }
        catch(ArgumentException) { Rig = null; return false; }
        _capability = cap; _skeleton = skeleton; _poseInfo = cap.SkeletonPosing.PoseInfo; _head = head;
        Values = new();
        Snapshot(cap);
        // Editing facial bones must not race a moving facial animation.
        // The existing freeze API retains the current visible animation frame.
        if(cap.Actor.TryGetCapability<ActionTimelineCapability>(out var timeline)) timeline.SetOverallSpeedOverride(0f);
        return true;
    }

    public void Apply(FaceControlValues values)
    {
        if(!IsValid() || Rig is null) return;
        Values = values.Copy();
        QueueTargets(Rig.Solve(Values), Rig.Head, true);
    }

    public bool ApplyStoredExpression(LibraryPoseDocument document)
    {
        if(!_gpose.IsGPosing || !_entities.TryGetCapabilityFromSelectedEntity<PosingCapability>(out var cap, considerParents: false)
            || cap.SkeletonPosing.CharacterSkeleton is not { IsValid: true } skeleton || cap.SkeletonPosing.HasTransitiveActions)
            return false;
        var head = skeleton.Bones.FirstOrDefault(b => b.Name == "j_kao");
        if(head is null || !PoseGeometry.IsFinite(head.LastRawTransform)) return false;
        var targets = new Dictionary<BonePoseInfoId, Transform>();
        foreach(var bone in skeleton.Bones)
            if(!bone.IsPartialRoot && bone.IsFaceBone && PoseGeometry.IsFacialBone(bone.Name)
                && document.Bones.TryGetValue(bone.Name, out var transform) && PoseGeometry.IsFinite(transform))
                targets.TryAdd(new(bone.Name, bone.PartialId, PoseInfoSlot.Character), transform);
        if(targets.Count == 0) return false;
        var hasAnchor = document.Bones.TryGetValue("j_kao", out var anchor) && PoseGeometry.IsFinite(anchor);
        End();
        _capability = cap; _skeleton = skeleton; _poseInfo = cap.SkeletonPosing.PoseInfo; _head = head;
        Snapshot(cap);
        // A saved head transform is a coordinate reference, never an edit to
        // the head. Partial files without one can still contribute rotations.
        QueueTargets(targets, hasAnchor ? (Transform)anchor! : head.LastRawTransform,
            hasAnchor && targets.Values.Any(t => t.Position.LengthSquared() > 0.00000001f));
        _commit = _endAfterCommit = true;
        return true;
    }

    private void QueueTargets(Dictionary<BonePoseInfoId, Transform> solved, Transform sourceHead, bool applyPosition)
    {
        var generation = ++_revision;
        var cap = _capability!; var skeleton = _skeleton!; var head = _head!;
        if(!Matrix4x4.Invert(FacePoseSolver.HeadFrame(sourceHead), out var inverse)) return;
        _dirty = true;
        cap.SkeletonPosing.RegisterTransitiveAction((bone, info) =>
        {
            if(generation != _revision || !ReferenceEquals(bone.Skeleton, skeleton) || bone.Freeze || !solved.TryGetValue(info.Id, out var target))
                return;
            if(!IsValid() || !PoseGeometry.IsFinite(head.LastRawTransform)) return;
            if(!FacePoseSolver.TryRebase(target, inverse * FacePoseSolver.HeadFrame(head.LastRawTransform), out var desired)) return;
            if(!applyPosition) desired.Position = bone.LastRawTransform.Position;
            // Merge this editor's unpropagated delta rather than append one
            // stack per mouse event. All descendants are solved explicitly.
            ApplyFaceDelta(info, desired, bone.LastRawTransform);
        });
    }

    internal static void ApplyFaceDelta(BonePoseInfo info, Transform desired, Transform current)
    {
        if(!PoseGeometry.IsFinite(desired) || !PoseGeometry.IsFinite(current)) return;
        var delta = desired.CalculateDiff(current);
        delta.Filter(TransformComponents.Position | TransformComponents.Rotation);
        if(delta.IsApproximatelySame(Transform.Identity)) return;
        var stacks = info.Stacks;
        if(stacks.Count > 0 && stacks[^1].PropagateComponents == TransformComponents.None && stacks[^1].IKInfo.Equals(BoneIKInfo.Disabled))
        {
            // Identity is a valid result when returning to the captured face.
            // Compose on the right, just as the skeleton applies rotations.
            stacks[^1] = stacks[^1] with { Transform = stacks[^1].Transform + delta };
        }
        else stacks.Add(new(TransformComponents.None, BoneIKInfo.Disabled, delta));
    }

    private void Snapshot(PosingCapability cap)
    {
        // These deltas already solve every facial descendant independently.
        // Do not run the general head reconciliation/import pass: it would
        // queue old cached transforms over a slider's just-committed result.
        _history.Snapshot(cap.Entity.Id, cap, new PosingCapability.PoseStack(cap.SkeletonPosing.PoseInfo.Clone(), cap.ModelPosing.Transform));
    }

    public void Commit() => _commit = true;
    private void OnUpdateEnd()
    {
        if(_commit && _dirty && IsValid())
        {
            _commit = _dirty = false;
            Snapshot(_capability!);
            if(_endAfterCommit) End();
        }
    }

    private bool IsValid() => IsLive() && ReferenceEquals(_entities.SelectedEntity, _capability!.Entity);
    private bool IsLive() => _gpose.IsGPosing && _capability is { } cap && _skeleton is { IsValid: true }
        && _entities.TryGetEntity(cap.Entity.Id, out var entity) && ReferenceEquals(entity, cap.Entity)
        && cap.Entity.TryGetCapability<SkeletonPosingCapability>(out var posing)
        && ReferenceEquals(posing.CharacterSkeleton, _skeleton)
        && ReferenceEquals(posing.PoseInfo, _poseInfo);

    public void End()
    {
        if(_dirty && IsLive()) Snapshot(_capability!);
        _revision++; _capability = null; _skeleton = null; _poseInfo = null; _head = null;
        Rig = null; Values = new(); _dirty = _commit = _endAfterCommit = false;
    }
    private void OnGPose(bool active) { if(!active) End(); }
    public void Dispose()
    {
        End(); _skeletons.SkeletonUpdateEnd -= OnUpdateEnd; _gpose.OnGPoseStateChange -= OnGPose;
    }
}
