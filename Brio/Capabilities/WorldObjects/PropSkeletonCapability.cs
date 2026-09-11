using Brio.Core;
using Brio.Entities.Core;
using Brio.Game.Actor.Interop;
using Brio.Game.Posing;
using Brio.Game.Posing.Skeletons;
using Brio.Game.WorldObjects.Objects;
using Brio.Resources;
using Brio.UI.Widgets.WorldObjects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static FFXIVClientStructs.Havok.Animation.Rig.hkaPose;

namespace Brio.Capabilities.WorldObjects;

public class PropSkeletonCapability : WorldObjectCapability
{
    private readonly SkeletonService _skeletonService;
    private readonly BrioPropObject _prop;
    private Skeleton? _skeleton;
    private int _generation = -1;
    private readonly List<PropBone> _bones = [];
    private Stack<Dictionary<string, Transform>> _undo = [];
    private readonly Stack<Dictionary<string, Transform>> _redo = [];
    private Dictionary<string, Transform>? _editStart;

    public IReadOnlyList<PropBone> Bones => _bones;
    public bool IsReady => _prop.IsValid && !_prop.IsDirty && _skeleton?.IsValid == true
        && _generation == _prop.SkeletonGeneration && _bones.Count > 0;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasOverrides => _prop.BoneTransforms.Count > 0;

    public PropSkeletonCapability(Entity parent, SkeletonService skeletonService) : base(parent)
    {
        _prop = (BrioPropObject)GameBgObject;
        _skeletonService = skeletonService;
        _skeletonService.SkeletonUpdateStart += UpdateSkeleton;
        Widget = new PropSkeletonWidget(this);
    }

    public bool IsOverridden(PropBone bone) => _prop.BoneTransforms.ContainsKey(bone.Key);

    public void BeginEdit() => _editStart ??= new(_prop.BoneTransforms);

    public void SetTransform(PropBone bone, Transform transform)
    {
        if(!IsReady || !_bones.Contains(bone) || !IsFinite(transform))
            return;

        BeginEdit();
        transform.Rotation = Quaternion.Normalize(transform.Rotation);
        _prop.BoneTransforms[bone.Key] = transform;
        bone.Current = transform;
    }

    public void CommitEdit()
    {
        if(_editStart is null)
            return;

        if(!_editStart.OrderBy(p => p.Key).SequenceEqual(_prop.BoneTransforms.OrderBy(p => p.Key)))
        {
            _undo.Push(_editStart);
            if(_undo.Count > 100)
                _undo = _undo.Trim(100);
            _redo.Clear();
        }
        _editStart = null;
    }

    public void ResetBone(PropBone bone)
    {
        BeginEdit();
        _prop.BoneTransforms.Remove(bone.Key);
        bone.Current = bone.Original;
        CommitEdit();
    }

    public void ResetAll()
    {
        BeginEdit();
        _prop.BoneTransforms.Clear();
        CommitEdit();
    }

    public void Undo()
    {
        CommitEdit();
        if(_undo.TryPop(out var state))
        {
            _redo.Push(new(_prop.BoneTransforms));
            _prop.RestoreBoneTransforms(state);
        }
    }

    public void Redo()
    {
        if(_redo.TryPop(out var state))
        {
            _undo.Push(new(_prop.BoneTransforms));
            _prop.RestoreBoneTransforms(state);
        }
    }

    // Run after the engine's animation/physics pass, at the same stage used by
    // actor posing. Only managed values are exposed to the UI; native poses are
    // resolved here and never retained by a deferred callback.
    private unsafe void UpdateSkeleton()
    {
        if(!_prop.IsValid || _prop.IsDirty)
            return;

        var characterBase = (BrioCharacterBase*)_prop.Address;
        var skeleton = _skeletonService.GetOrCacheStandaloneSkeleton(characterBase);
        if(skeleton is null || !skeleton.IsValid
            || skeleton.GameSkeleton != characterBase->CharacterBase.Skeleton)
        {
            _skeleton = null;
            _bones.Clear();
            return;
        }

        if(_skeleton != skeleton || _generation != _prop.SkeletonGeneration)
        {
            _skeleton = skeleton;
            _generation = _prop.SkeletonGeneration;
            _bones.Clear();
            _undo.Clear();
            _redo.Clear();
            _editStart = null;

            foreach(var bone in skeleton.Bones)
            {
                var pose = bone.Partial.GetBestPose();
                if(pose == null || pose->Skeleton == null || bone.Index >= pose->Skeleton->Bones.Length)
                    continue;

                Transform original = pose->AccessBoneModelSpace(bone.Index, PropagateOrNot.DontPropagate);
                _bones.Add(new PropBone(bone, original));
            }
        }

        foreach(var bone in _bones)
        {
            var pose = bone.NativeBone.Partial.GetBestPose();
            if(pose == null || pose->Skeleton == null || bone.NativeBone.Index >= pose->Skeleton->Bones.Length)
                continue;

            var overridden = _prop.BoneTransforms.TryGetValue(bone.Key, out var transform) && IsFinite(transform);
            if(overridden || bone.WasOverridden)
            {
                if(!overridden)
                    transform = bone.Original;
                else
                    transform.Rotation = Quaternion.Normalize(transform.Rotation);

                // Absolute model-space values do not accumulate each frame on
                // static props. DontPropagate lets sibling/child parts remain
                // in place when one mesh's controlling bone is scaled down.
                var target = pose->AccessBoneModelSpace(bone.NativeBone.Index, PropagateOrNot.DontPropagate);
                target->Translation = new() { X = transform.Position.X, Y = transform.Position.Y, Z = transform.Position.Z, W = 0 };
                target->Rotation = new() { X = transform.Rotation.X, Y = transform.Rotation.Y, Z = transform.Rotation.Z, W = transform.Rotation.W };
                target->Scale = new() { X = transform.Scale.X, Y = transform.Scale.Y, Z = transform.Scale.Z, W = 0 };
            }

            bone.WasOverridden = overridden;
            bone.Current = pose->AccessBoneModelSpace(bone.NativeBone.Index, PropagateOrNot.DontPropagate);
        }
    }

    private static bool IsFinite(Transform transform)
        => float.IsFinite(transform.Position.X) && float.IsFinite(transform.Position.Y) && float.IsFinite(transform.Position.Z)
        && float.IsFinite(transform.Scale.X) && float.IsFinite(transform.Scale.Y) && float.IsFinite(transform.Scale.Z)
        && float.IsFinite(transform.Rotation.X) && float.IsFinite(transform.Rotation.Y)
        && float.IsFinite(transform.Rotation.Z) && float.IsFinite(transform.Rotation.W)
        && float.IsFinite(transform.Rotation.LengthSquared())
        && transform.Rotation.LengthSquared() > 0.000001f;

    public override void OnEntityDeselected() => CommitEdit();

    public override void Dispose()
    {
        _skeletonService.SkeletonUpdateStart -= UpdateSkeleton;
        _skeleton = null;
        _bones.Clear();
        _undo.Clear();
        _redo.Clear();
        _editStart = null;
        base.Dispose();
    }
}

public sealed class PropBone(Bone bone, Transform original)
{
    internal Bone NativeBone { get; } = bone;
    internal bool WasOverridden { get; set; }
    public string Key { get; } = $"{bone.PartialId}:{bone.Name}";
    public string Name => NativeBone.Name;
    public string DisplayName => Localize.GetNullable($"bones.{Name}") ?? Localize.Format("Bone {0}", Name);
    public string Description => NativeBone.FriendlyDescriptor;
    public Transform Original { get; } = original;
    public Transform Current { get; internal set; } = original;
}
