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
    private bool _dirty, _commit, _endAfterCommit, _hasUndoBaseline;
    private readonly Dictionary<BonePoseInfoId, Transform> _appliedTargets = [];
    public FacePoseSolver? Rig { get; private set; }
    public FaceControlValues Values { get; private set; } = new();
    public bool IsBound => IsValid();

    public FaceEditingService(EntityManager entities, GPoseService gpose, SkeletonService skeletons, HistoryService history)
    {
        _entities = entities; _gpose = gpose; _skeletons = skeletons; _history = history;
        skeletons.SkeletonUpdateEnd += OnUpdateEnd;
        gpose.OnGPoseStateChange += OnGPose;
    }

    internal readonly record struct Target(PosingCapability Capability, Skeleton Skeleton, PoseInfo PoseInfo);

    internal Target? GetTarget()
    {
        if(!_gpose.IsGPosing || !_entities.TryGetCapabilityFromSelectedEntity<PosingCapability>(out var cap, considerParents: false)
            || !cap.Entity.TryGetCapability<SkeletonPosingCapability>(out var posing)
            || posing.CharacterSkeleton is not { IsValid: true } skeleton)
            return null;
        return new(cap, skeleton, posing.PoseInfo);
    }

    internal static IReadOnlySet<FaceControl> DetectControls(Skeleton skeleton)
        => FacePoseSolver.DetectControls(skeleton.Bones.Where(IsEditableFaceBone).Select(b => b.Name));

    private static bool IsEditableFaceBone(Bone bone)
        => !bone.IsPartialRoot && bone.Name.StartsWith("j_f_", StringComparison.Ordinal) && bone.IsFaceBone;

    public bool Capture()
    {
        if(GetTarget() is not { } target || target.Capability.SkeletonPosing.HasTransitiveActions) return false;
        var (cap, skeleton, poseInfo) = target;
        var head = skeleton.Bones.FirstOrDefault(b => b.Name == "j_kao");
        if(head is null) return false;
        var bones = skeleton.Bones.Where(IsEditableFaceBone)
            .Select(b => new FaceRigBone(new(b.Name, b.PartialId, PoseInfoSlot.Character),
                b.Parent is { } parent ? new BonePoseInfoId(parent.Name, parent.PartialId, PoseInfoSlot.Character) : null, b.LastTransform)).ToArray();
        FacePoseSolver rig;
        try { rig = new FacePoseSolver(head.LastTransform, bones); }
        catch(ArgumentException) { return false; }
        End();
        _capability = cap; _skeleton = skeleton; _poseInfo = poseInfo; _head = head; Rig = rig;
        return true;
    }

    public void FreezeCurrentFrame()
    {
        if(GetTarget() is { } target && target.Capability.Actor.TryGetCapability<ActionTimelineCapability>(out var timeline))
            timeline.SetOverallSpeedOverride(0f);
    }

    public bool Apply(FaceControlValues values)
    {
        if((!IsValid() || Rig is null) && (values.IsZero || !Capture())) return false;
        var rig = Rig!;
        Values = values.Copy();
        var targets = rig.Solve(Values);
        foreach(var bone in rig.Bones)
        {
            // Do not pin unrelated facial bones to the captured frame. Compare
            // to applied (not just queued) edits so rapid input can replace an
            // older queued edit or return to zero before the skeleton updates.
            if(targets[bone.Id].IsApproximatelySame(_appliedTargets.GetValueOrDefault(bone.Id, bone.Transform)))
                targets.Remove(bone.Id);
        }
        if(targets.Count == 0)
        {
            _revision++;
            return true;
        }
        if(!_hasUndoBaseline)
        {
            Snapshot(_capability!);
            _hasUndoBaseline = true;
        }
        QueueTargets(targets, rig.Head, true, rememberSliderTargets: true);
        return true;
    }

    public bool ApplyStoredExpression(LibraryPoseDocument document)
    {
        if(GetTarget() is not { } target || target.Capability.SkeletonPosing.HasTransitiveActions)
            return false;
        var (cap, skeleton, poseInfo) = target;
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
        _capability = cap; _skeleton = skeleton; _poseInfo = poseInfo; _head = head;
        Snapshot(cap);
        // A saved head transform is a coordinate reference, never an edit to
        // the head. Partial files without one can still contribute rotations.
        QueueTargets(targets, hasAnchor ? (Transform)anchor! : head.LastRawTransform,
            hasAnchor && targets.Values.Any(t => t.Position.LengthSquared() > 0.00000001f));
        _commit = _endAfterCommit = true;
        return true;
    }

    private void QueueTargets(Dictionary<BonePoseInfoId, Transform> solved, Transform sourceHead, bool applyPosition, bool rememberSliderTargets = false)
    {
        var generation = ++_revision;
        var cap = _capability!; var skeleton = _skeleton!; var head = _head!;
        if(!Matrix4x4.Invert(FacePoseSolver.HeadFrame(sourceHead), out var inverse)) return;
        cap.SkeletonPosing.RegisterTransitiveAction((bone, info) =>
        {
            if(generation != _revision || !ReferenceEquals(bone.Skeleton, skeleton) || bone.Freeze || !solved.TryGetValue(info.Id, out var target))
                return;
            if(!IsValid() || !PoseGeometry.IsFinite(head.LastRawTransform)) return;
            if(!FacePoseSolver.TryRebase(target, inverse * FacePoseSolver.HeadFrame(head.LastRawTransform), out var desired)) return;
            if(!applyPosition) desired.Position = bone.LastRawTransform.Position;
            // Merge this editor's unpropagated delta rather than append one
            // stack per mouse event. All descendants are solved explicitly.
            if(!PoseGeometry.IsFinite(desired) || !PoseGeometry.IsFinite(bone.LastRawTransform)) return;
            _dirty |= ApplyFaceDelta(info, desired, bone.LastRawTransform);
            if(rememberSliderTargets) _appliedTargets[info.Id] = target;
        });
    }

    internal static bool ApplyFaceDelta(BonePoseInfo info, Transform desired, Transform current)
    {
        if(!PoseGeometry.IsFinite(desired) || !PoseGeometry.IsFinite(current)) return false;
        var delta = desired.CalculateDiff(current);
        delta.Filter(TransformComponents.Position | TransformComponents.Rotation);
        if(delta.IsApproximatelySame(Transform.Identity)) return false;
        var stacks = info.Stacks;
        if(stacks.Count > 0 && stacks[^1].PropagateComponents == TransformComponents.None && stacks[^1].IKInfo.Equals(BoneIKInfo.Disabled))
        {
            // Identity is a valid result when returning to the captured face.
            // Compose on the right, just as the skeleton applies rotations.
            stacks[^1] = stacks[^1] with { Transform = stacks[^1].Transform + delta };
        }
        else stacks.Add(new(TransformComponents.None, BoneIKInfo.Disabled, delta));
        return true;
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
        if(_commit && IsValid())
        {
            _commit = false;
            if(_dirty)
            {
                _dirty = false;
                Snapshot(_capability!);
            }
            if(_endAfterCommit) End();
        }
        else if(_endAfterCommit && !IsValid()) End();
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
        _appliedTargets.Clear();
        Rig = null; Values = new(); _dirty = _commit = _endAfterCommit = _hasUndoBaseline = false;
    }
    private void OnGPose(bool active) { if(!active) End(); }
    public void Dispose()
    {
        End(); _skeletons.SkeletonUpdateEnd -= OnUpdateEnd; _gpose.OnGPoseStateChange -= OnGPose;
    }
}
