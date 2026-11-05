using Brio.Core;
using Brio.Files;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Brio.Services.Poses;

public static class PoseGeometry
{
    public static bool IsFinite(PoseData.Bone bone) => IsFinite((Transform)bone);
    public static bool IsFinite(Transform bone) => Finite(bone.Position) && Finite(bone.Scale)
        && float.IsFinite(bone.Rotation.X) && float.IsFinite(bone.Rotation.Y) && float.IsFinite(bone.Rotation.Z)
        && float.IsFinite(bone.Rotation.W) && float.IsFinite(bone.Rotation.LengthSquared()) && bone.Rotation.LengthSquared() > 0.000001f;
    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)
        && Math.Max(Math.Max(Math.Abs(v.X), Math.Abs(v.Y)), Math.Abs(v.Z)) <= 100000;
    public static bool IsFacialBone(string name) => name.StartsWith("j_f_", StringComparison.Ordinal) || name == "j_ago";
    public static bool IsFaceName(string name) => name == "j_kao" || IsFacialBone(name);
    public static bool IsHandName(string name) => name.StartsWith("j_te_") || new[] { "hito", "naka", "kusu", "ko_", "oya" }.Any(x => name.StartsWith("j_" + x) || name.StartsWith("iv_" + x));

    public static LibraryPoseDocument Select(PoseFile source, PoseAssetKind kind, HashSet<string>? faceBones = null)
    {
        var result = JsonSerializer.Deserialize<LibraryPoseDocument>(JsonSerializer.Serialize(source));
        result.Bones = result.Bones.Where(x => kind switch
        {
            PoseAssetKind.Expression => IsFaceName(x.Key) && (faceBones?.Contains(x.Key) ?? true),
            PoseAssetKind.Hands => IsHandName(x.Key),
            PoseAssetKind.Body => !(faceBones?.Contains(x.Key) ?? IsFaceName(x.Key)),
            _ => true,
        }).ToDictionary(x => x.Key, x => x.Value);
        if(kind is not PoseAssetKind.Complete)
        {
            result.MainHand.Clear(); result.OffHand.Clear(); result.Prop.Clear(); result.Ornament.Clear();
        }
        result.Base64Image = null;
        return result;
    }

    public static readonly (string Parent, string Child)[] BodyLinks = BuildLinks();
    private static (string, string)[] BuildLinks()
    {
        var links = new List<(string, string)> { ("n_root", "n_hara"), ("n_hara", "j_kosi"), ("j_kosi", "j_sebo_a"), ("j_sebo_a", "j_sebo_b"), ("j_sebo_b", "j_sebo_c"), ("j_sebo_c", "j_kubi"), ("j_kubi", "j_kao") };
        foreach(var side in new[] { "l", "r" })
        {
            void Chain(params string[] names) { for(var i = 1; i < names.Length; i++) links.Add((names[i - 1], names[i])); }
            Chain("j_sebo_c", $"j_sako_{side}", $"j_ude_a_{side}", $"j_ude_b_{side}", $"j_te_{side}");
            Chain("j_kosi", $"j_asi_a_{side}", $"j_asi_b_{side}", $"j_asi_c_{side}", $"j_asi_d_{side}", $"j_asi_e_{side}");
            foreach(var finger in new[] { "oya", "hito", "naka", "kusu", "ko" })
                Chain($"j_te_{side}", $"j_{finger}_a_{side}", $"j_{finger}_b_{side}", $"iv_{finger}_c_{side}");
        }
        return [.. links];
    }
}
