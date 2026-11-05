using Brio.Resources;
using Brio.Services.Poses;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Brio.UI.Controls.Stateless;

public sealed class PosePreviewView
{
    public float Yaw = -0.3f, Pitch = 0.05f, Zoom = 1;
    public void Reset() { Yaw = -0.3f; Pitch = 0.05f; Zoom = 1; }
}

public static class PoseMannequin
{
    public static Vector4 Color(PoseAssetGender gender) => gender switch
    {
        PoseAssetGender.Male => new(.35f, .65f, 1, 1),
        PoseAssetGender.Female => new(1, .4f, .45f, 1),
        PoseAssetGender.Any => new(.35f, .85f, .75f, 1),
        _ => new(.78f, .8f, .84f, 1),
    };

    public static void Draw(string id, LibraryPoseDocument? pose, PoseAssetMetadata metadata, Vector2 size, PosePreviewView? view = null)
    {
        var top = ImGui.GetCursorScreenPos();
        size = Vector2.Max(size, new(20));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(top, top + size, 0xFF23201E, 5);
        draw.PushClipRect(top, top + size, true);
        if(pose is null)
        {
            draw.AddText(top + new Vector2(8), 0xFFAAAAAA, Localize.Text("Loading or unavailable pose"));
        }
        else
        {
            var points = pose.Bones.Where(x => PoseGeometry.IsFinite(x.Value)
                && (metadata.Kind != PoseAssetKind.Expression || PoseGeometry.IsFaceName(x.Key))
                && (metadata.Kind != PoseAssetKind.Hands || PoseGeometry.IsHandName(x.Key)))
                .ToDictionary(x => x.Key, x => x.Value.Position);
            var links = PoseGeometry.BodyLinks.Where(p => points.ContainsKey(p.Parent) && points.ContainsKey(p.Child)).ToList();
            if(metadata.Kind == PoseAssetKind.Expression)
            {
                links.Clear();
                foreach(var (child, parent) in metadata.Parents)
                    if(points.ContainsKey(parent) && points.ContainsKey(child)) links.Add((parent, child));
            }
            if(links.Count == 0 && points.Count > 1)
            {
                // Imported partial poses have no hierarchy. A sparse nearest-
                // neighbour wireframe is a structural preview, not a full rig.
                var sparse = points.Take(200).ToArray();
                foreach(var p in sparse)
                    foreach(var other in sparse.Where(q => q.Key != p.Key)
                        .OrderBy(q => Vector3.DistanceSquared(p.Value, q.Value)).Take(2))
                        links.Add((p.Key, other.Key));
            }
            var visibleNames = links.SelectMany(x => new[] { x.Item1, x.Item2 }).ToHashSet();
            if(visibleNames.Count == 0) visibleNames.UnionWith(points.Keys);
            var useful = points.Where(p => visibleNames.Contains(p.Key)).ToArray();
            if(useful.Length > 0)
            {
                var min = useful.Select(x => x.Value).Aggregate(Vector3.Min);
                var max = useful.Select(x => x.Value).Aggregate(Vector3.Max);
                var center = (min + max) / 2;
                var rotation = Matrix4x4.CreateRotationY(view?.Yaw ?? -0.3f) * Matrix4x4.CreateRotationX(view?.Pitch ?? .05f);
                var projected = useful.ToDictionary(x => x.Key, x => Vector3.Transform(x.Value - center, rotation));
                var projectedMin = projected.Values.Aggregate(Vector3.Min);
                var projectedMax = projected.Values.Aggregate(Vector3.Max);
                var span = projectedMax - projectedMin;
                if(Math.Max(span.X, span.Y) < 0.00001f)
                    draw.AddText(top + new Vector2(8), 0xFFAAAAAA, Localize.Text("No spatial bone data for preview"));
                else
                {
                    var scale = Math.Min((size.X - 24) / Math.Max(span.X, .00001f), (size.Y - 24) / Math.Max(span.Y, .00001f)) * (view?.Zoom ?? 1);
                    var offset = top + size / 2;
                    var midpoint = (projectedMin + projectedMax) / 2;
                    Vector2 Screen(Vector3 p) => offset + new Vector2(p.X - midpoint.X, -(p.Y - midpoint.Y)) * scale;
                    var color = Color(metadata.Gender);
                    var depth = Math.Max(max.Z - min.Z, .001f);
                    var lineWidth = metadata.Kind == PoseAssetKind.Expression ? 1.2f : Math.Clamp(Math.Min(size.X, size.Y) / 38, 2, 8);
                    foreach(var (parent, child) in links.OrderByDescending(l => (projected[l.Item1].Z + projected[l.Item2].Z) / 2))
                    {
                        var a = projected[parent]; var b = projected[child];
                        var shade = Math.Clamp(.75f - (a.Z + b.Z) / (depth * 5), .4f, 1);
                        draw.AddLine(Screen(a), Screen(b), ImGui.ColorConvertFloat4ToU32(color * new Vector4(shade, shade, shade, 1)), lineWidth);
                    }
                    var jointColor = ImGui.ColorConvertFloat4ToU32(color);
                    foreach(var (_, p) in projected)
                        draw.AddCircleFilled(Screen(p), Math.Max(1.5f, lineWidth * .65f), jointColor);
                    if(metadata.Kind is not PoseAssetKind.Expression && projected.TryGetValue("j_kao", out var head))
                        draw.AddCircle(Screen(head), Math.Clamp(Math.Min(size.X, size.Y) * .045f, 4, 15), jointColor, 20, lineWidth * .6f);
                }
            }
        }
        draw.PopClipRect();
        if(view is null) { ImGui.Dummy(size); return; }
        ImGui.InvisibleButton(id, size);
        if(ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var delta = ImGui.GetIO().MouseDelta;
            view.Yaw += delta.X * .01f;
            view.Pitch = Math.Clamp(view.Pitch + delta.Y * .01f, -1.5f, 1.5f);
        }
        if(ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
            view.Zoom = Math.Clamp(view.Zoom * MathF.Pow(1.12f, ImGui.GetIO().MouseWheel), .25f, 4);
    }
}
