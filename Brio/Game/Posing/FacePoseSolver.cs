using Brio.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Brio.Game.Posing;

public enum FaceControl { JawOpening, MouthCorners, Pucker, UpperLip, Eyelids, Squint, BrowHeight, InnerBrow }

public sealed class FaceControlValues
{
    public float[] Left { get; set; } = new float[8];
    public float[] Right { get; set; } = new float[8];
    public FaceControlValues Copy() => new() { Left = (float[])Left.Clone(), Right = (float[])Right.Clone() };
    public float Get(FaceControl control, string name)
    {
        var i = (int)control;
        var value = name.EndsWith("_l", StringComparison.Ordinal) ? Left[i]
            : name.EndsWith("_r", StringComparison.Ordinal) ? Right[i] : (Left[i] + Right[i]) / 2;
        return float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
    }
    public bool IsZero => Left.Concat(Right).All(v => v == 0);
}

public sealed record FaceRigBone(BonePoseInfoId Id, BonePoseInfoId? Parent, Transform Transform);

// Small, deterministic geometric controls. This is not an IK solver, animation
// loader or a universal sculpt profile: all distances come from this face.
public sealed class FacePoseSolver
{
    public FaceRigBone[] Bones { get; }
    public Transform Head { get; }
    private readonly Matrix4x4 _headFrame;
    public IReadOnlySet<FaceControl> Available { get; }
    private readonly Dictionary<BonePoseInfoId, Transform> _local;
    private readonly HashSet<BonePoseInfoId> _jawChildren = [];
    private readonly Vector3 _left, _up, _forward, _eyeMid;
    private readonly float _width;
    private readonly BonePoseInfoId? _jaw;

    public FacePoseSolver(Transform head, FaceRigBone[] bones)
    {
        Head = head;
        Bones = bones;
        static bool Valid(Transform t) => float.IsFinite(t.Position.X) && float.IsFinite(t.Position.Y) && float.IsFinite(t.Position.Z)
            && float.IsFinite(t.Scale.X) && float.IsFinite(t.Scale.Y) && float.IsFinite(t.Scale.Z)
            && float.IsFinite(t.Rotation.X) && float.IsFinite(t.Rotation.Y) && float.IsFinite(t.Rotation.Z) && float.IsFinite(t.Rotation.W)
            && float.IsFinite(t.Rotation.LengthSquared()) && t.Rotation.LengthSquared() > .000001f
            && Math.Max(Math.Max(Math.Abs(t.Position.X), Math.Abs(t.Position.Y)), Math.Abs(t.Position.Z)) <= 100000;
        if(!Valid(head) || bones.Length > 2048 || bones.Any(b => !Valid(b.Transform))) throw new ArgumentException("Invalid facial reference.");
        _headFrame = HeadFrame(head);
        if(!Matrix4x4.Invert(_headFrame, out var inverse)) throw new ArgumentException("Invalid head transform.");
        _local = bones.ToDictionary(x => x.Id, x => Rebase(x.Transform, inverse));
        Vector3 Find(string name) => _local.FirstOrDefault(x => x.Key.BoneName == name).Value.Position;
        if(!bones.Any(x => x.Id.BoneName == "j_f_eye_l") || !bones.Any(x => x.Id.BoneName == "j_f_eye_r"))
            throw new ArgumentException("A Dawntrail facial skeleton with two eye bones is required.");
        var leftEye = Find("j_f_eye_l"); var rightEye = Find("j_f_eye_r");
        _width = Vector3.Distance(leftEye, rightEye);
        if(!float.IsFinite(_width) || _width < 0.00001f) throw new ArgumentException("Invalid facial proportions.");
        _left = Vector3.Normalize(leftEye - rightEye);
        _eyeMid = (leftEye + rightEye) / 2;
        var lips = _local.Where(x => x.Key.BoneName.Contains("lip", StringComparison.Ordinal)).Select(x => x.Value.Position).ToArray();
        var mouth = lips.Length > 0 ? lips.Aggregate(Vector3.Zero, (a, b) => a + b) / lips.Length : _eyeMid - Vector3.UnitY * _width;
        var up = _eyeMid - mouth;
        up -= _left * Vector3.Dot(up, _left);
        if(up.LengthSquared() < 0.00000001f) throw new ArgumentException("Invalid face orientation.");
        _up = Vector3.Normalize(up);
        _forward = Vector3.Normalize(Vector3.Cross(_left, _up));
        var nose = bones.FirstOrDefault(x => x.Id.BoneName == "j_f_uhana");
        if(nose is not null && Vector3.Dot(_local[nose.Id].Position - _eyeMid, _forward) < 0) _forward = -_forward;
        _jaw = bones.FirstOrDefault(x => x.Id.BoneName == "j_f_ago")?.Id;
        var parents = bones.ToDictionary(x => x.Id, x => x.Parent);
        foreach(var bone in bones)
        {
            BonePoseInfoId? next = bone.Id;
            for(var n = 0; n < 256 && next is { } id; n++)
            {
                if(id == _jaw) { _jawChildren.Add(bone.Id); break; }
                next = parents.GetValueOrDefault(id);
            }
        }
        var available = new HashSet<FaceControl>();
        if(_jaw is not null) available.Add(FaceControl.JawOpening);
        if(lips.Length > 0) available.UnionWith([FaceControl.MouthCorners, FaceControl.Pucker, FaceControl.UpperLip]);
        if(bones.Any(x => IsUpperLid(x.Id.BoneName))) available.UnionWith([FaceControl.Eyelids, FaceControl.Squint]);
        if(bones.Any(x => IsBrow(x.Id.BoneName))) available.UnionWith([FaceControl.BrowHeight, FaceControl.InnerBrow]);
        Available = available;
    }

    public Dictionary<BonePoseInfoId, Transform> Solve(FaceControlValues controls)
    {
        if(controls.IsZero) return Bones.ToDictionary(x => x.Id, x => x.Transform);
        var result = new Dictionary<BonePoseInfoId, Transform>();
        var jawAmount = Math.Max(0, controls.Get(FaceControl.JawOpening, ""));
        var jawPivot = _jaw is { } j ? _local[j].Position : Vector3.Zero;
        var jawRotation = Quaternion.CreateFromAxisAngle(_left, jawAmount * 0.3f);
        // Pick the opening sign geometrically instead of assuming a model axis.
        var lowerLip = _local.FirstOrDefault(x => x.Key.BoneName.Contains("dlip", StringComparison.Ordinal)).Value.Position;
        if(Vector3.Dot(Vector3.Transform(lowerLip - jawPivot, jawRotation) - (lowerLip - jawPivot), _up) > 0)
            jawRotation = Quaternion.Conjugate(jawRotation);
        foreach(var node in Bones)
        {
            var name = node.Id.BoneName;
            var t = _local[node.Id];
            if(_jawChildren.Contains(node.Id))
            {
                t.Position = jawPivot + Vector3.Transform(t.Position - jawPivot, jawRotation);
                t.Rotation = Quaternion.Normalize(jawRotation * t.Rotation);
            }
            var x = Vector3.Dot(_local[node.Id].Position - _eyeMid, _left);
            var side = MathF.Sign(x);
            if(name.Contains("lip", StringComparison.Ordinal))
            {
                var cornerWeight = Math.Clamp(MathF.Abs(x) / (_width * 0.48f), 0, 1);
                var smile = controls.Get(FaceControl.MouthCorners, name);
                var pucker = Math.Max(0, controls.Get(FaceControl.Pucker, name));
                t.Position += _up * (_width * 0.075f * cornerWeight * smile)
                    + _left * (_width * 0.025f * side * cornerWeight * smile)
                    + _forward * (_width * 0.08f * pucker) - _left * (x * 0.15f * pucker);
                if(name.Contains("ulip") || name.Contains("umlip") || name.Contains("uslip"))
                    t.Position += _up * (_width * 0.045f * controls.Get(FaceControl.UpperLip, name));
            }
            if(IsUpperLid(name))
                t.Position -= _up * (_width * 0.055f * controls.Get(FaceControl.Eyelids, name));
            else if(name.Contains("mabdn", StringComparison.Ordinal))
                t.Position += _up * (_width * (0.018f * controls.Get(FaceControl.Eyelids, name) + 0.035f * controls.Get(FaceControl.Squint, name)));
            if(IsBrow(name))
            {
                var innerWeight = 1 - Math.Clamp(MathF.Abs(x) / (_width * 0.65f), 0, 1);
                t.Position += _up * (_width * (0.07f * controls.Get(FaceControl.BrowHeight, name) + 0.05f * innerWeight * controls.Get(FaceControl.InnerBrow, name)));
            }
            result[node.Id] = Rebase(t, _headFrame);
        }
        return result;
    }

    private static bool IsUpperLid(string n) => n.Contains("mabup", StringComparison.Ordinal) || n.StartsWith("j_f_mab_", StringComparison.Ordinal);
    private static bool IsBrow(string n) => n.Contains("mayu", StringComparison.Ordinal) || n.Contains("miken", StringComparison.Ordinal);
    public static Matrix4x4 HeadFrame(Transform head)
        => Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(head.Rotation)) * Matrix4x4.CreateTranslation(head.Position);
    public static Transform Rebase(Transform transform, Matrix4x4 frame)
    {
        if(!TryRebase(transform, frame, out var result)) throw new ArgumentException("Invalid facial transform.");
        return result;
    }

    public static bool TryRebase(Transform transform, Matrix4x4 frame, out Transform result)
    {
        result = default;
        if(!Matrix4x4.Decompose(transform.ToMatrix() * frame, out var s, out var q, out var p)) return false;
        result = new() { Position = p, Rotation = q, Scale = s };
        return true;
    }
}
