using Brio.Services.Poses;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Brio.UI.Controls.Stateless;

// Pure, off-screen renderer. The forms are fitted to saved model-space joints,
// never to a live actor. Incomplete/non-humanoid poses keep the wireframe.
internal static class PoseBodyPreview
{
    internal readonly record struct Camera(int Width, int Height, float Yaw, float Pitch, float Zoom);
    private readonly record struct Form(Vector3 Center, Vector3 X, Vector3 Y, Vector3 Z, float Tone);
    private readonly record struct Ring(Vector3 Center, Vector3 Across, float Width, float Depth);
    private readonly record struct Vertex(Vector3 Position, float Lightness);
    private readonly record struct Side(Vector3 Shoulder, Vector3 Elbow, Vector3 Wrist, Vector3 Hip, Vector3 Knee, Vector3 Ankle);
    internal const float DefaultYaw = -.62f, DefaultPitch = .09f;
    private const int RingSides = 16;
    private static readonly Vector3 Light = Vector3.Normalize(new(-.53f, .68f, .62f));

    internal static byte[]? Render(LibraryPoseDocument pose, Camera camera, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var forms = BuildForms(pose, out var torso);
        if(forms is null) return null;

        // 2x coverage sampling, orthographic view with the camera on +Z.
        var (width, height, yaw, pitch, zoom) = camera;
        var w = width * 2;
        var h = height * 2;
        var orbit = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch);
        var torsoVertices = BuildTorso(torso, orbit);
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        for(var i = 0; i < forms.Count; i++)
        {
            var f = forms[i];
            f = new(Vector3.Transform(f.Center, orbit), Vector3.TransformNormal(f.X, orbit),
                Vector3.TransformNormal(f.Y, orbit), Vector3.TransformNormal(f.Z, orbit), f.Tone);
            forms[i] = f;
            var p = new Vector2(f.Center.X, f.Center.Y);
            var extent = Extent(f);
            min = Vector2.Min(min, p - extent);
            max = Vector2.Max(max, p + extent);
        }
        foreach(var vertex in torsoVertices)
        {
            var p = new Vector2(vertex.Position.X, vertex.Position.Y);
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        var fit = Math.Min(w / (max.X - min.X), h / (max.Y - min.Y)) * .86f * zoom;
        var mid = (min + max) * .5f;
        var origin = new Vector3(mid, 0);
        var depth = ArrayPool<float>.Shared.Rent(w * h);
        var color = ArrayPool<uint>.Shared.Rent(w * h);
        try
        {
            depth.AsSpan(0, w * h).Fill(float.NegativeInfinity);
            color.AsSpan(0, w * h).Clear();
            foreach(var f in forms)
            {
                cancellation.ThrowIfCancellationRequested();
                Rasterize(f with { Center = f.Center - origin }, fit, depth, color, w, h);
            }
            RasterizeTorso(torsoVertices, mid, fit, depth, color, w, h, cancellation);
            return Resolve(depth, color, w, h, fit, cancellation);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(depth);
            ArrayPool<uint>.Shared.Return(color);
        }
    }

    private static Vector2 Extent(Form f) => new(
        MathF.Sqrt(f.X.X * f.X.X + f.Y.X * f.Y.X + f.Z.X * f.Z.X),
        MathF.Sqrt(f.X.Y * f.X.Y + f.Y.Y * f.Y.Y + f.Z.Y * f.Z.Y));

    private static void Rasterize(Form f, float fit, float[] depth, uint[] color, int w, int h)
    {
        var extent = Extent(f) * fit;
        var center = new Vector2(w * .5f + f.Center.X * fit, h * .5f - f.Center.Y * fit);
        var x0 = Math.Max(0, (int)MathF.Floor(center.X - extent.X));
        var x1 = Math.Min(w - 1, (int)MathF.Ceiling(center.X + extent.X));
        var y0 = Math.Max(0, (int)MathF.Floor(center.Y - extent.Y));
        var y1 = Math.Min(h - 1, (int)MathF.Ceiling(center.Y + extent.Y));

        // Intersect each orthographic ray with the oriented ellipsoid, in
        // its local unit sphere. Keep the nearest surface per pixel.
        var ax = f.X / f.X.LengthSquared();
        var ay = f.Y / f.Y.LengthSquared();
        var az = f.Z / f.Z.LengthSquared();
        var stepX = new Vector3(ax.X, ay.X, az.X) / fit;
        var stepY = -new Vector3(ax.Y, ay.Y, az.Y) / fit;
        var ray = new Vector3(ax.Z, ay.Z, az.Z);
        var a = ray.LengthSquared();
        for(var y = y0; y <= y1; y++)
        {
            var p = stepY * (y + .5f - center.Y) + stepX * (x0 + .5f - center.X);
            for(var x = x0; x <= x1; x++, p += stepX)
            {
                var b = Vector3.Dot(ray, p);
                var discriminant = b * b - a * (p.LengthSquared() - 1);
                if(discriminant <= 0) continue;
                var z = (MathF.Sqrt(discriminant) - b) / a;
                var i = y * w + x;
                if(z + f.Center.Z <= depth[i]) continue;
                var q = p + ray * z;
                var normal = Vector3.Normalize(q.X * ax + q.Y * ay + q.Z * az);
                depth[i] = z + f.Center.Z;
                color[i] = Paint(Illuminate(normal, f.Tone));
            }
        }
    }

    private static float Illuminate(Vector3 normal, float tone = 0) =>
        Math.Clamp(.50f + .36f * Vector3.Dot(normal, Light) + .12f * normal.Z + tone, .15f, 1);

    private static uint Paint(float light) =>
        (uint)(65 + 159 * light) | (uint)(91 + 148 * light) << 8 | (uint)(128 + 122 * light) << 16;

    private static Vertex[] BuildTorso(Ring[] rings, Matrix4x4 orbit)
    {
        // One continuous surface for the torso. Each cross section follows the
        // local spine direction, so waist bends don't turn it into stacked balls.
        var points = new Vector3[rings.Length * RingSides];
        var radial = new Vector3[points.Length];
        for(var i = 0; i < rings.Length; i++)
        {
            var ring = rings[i];
            var along = rings[Math.Min(i + 1, rings.Length - 1)].Center - rings[Math.Max(0, i - 1)].Center;
            Basis(along, ring.Across, out var x, out _, out var z);
            for(var j = 0; j < RingSides; j++)
            {
                var (s, c) = MathF.SinCos(j * MathF.Tau / RingSides);
                var offset = x * (ring.Width * c) + z * (ring.Depth * s);
                points[i * RingSides + j] = Vector3.Transform(ring.Center + offset, orbit);
                radial[i * RingSides + j] = Vector3.TransformNormal(offset, orbit);
            }
        }

        var vertices = new Vertex[points.Length];
        for(var i = 0; i < rings.Length; i++)
        for(var j = 0; j < RingSides; j++)
        {
            var k = i * RingSides + j;
            var around = points[i * RingSides + (j + 1) % RingSides]
                - points[i * RingSides + (j + RingSides - 1) % RingSides];
            var along = points[Math.Min(i + 1, rings.Length - 1) * RingSides + j]
                - points[Math.Max(0, i - 1) * RingSides + j];
            var normal = Direction(Vector3.Cross(around, along), Direction(radial[k], Vector3.UnitZ));
            if(Vector3.Dot(normal, radial[k]) < 0) normal = -normal;
            vertices[k] = new(points[k], Illuminate(normal, -.015f));
        }
        return vertices;
    }

    private static void RasterizeTorso(Vertex[] vertices, Vector2 center, float fit, float[] depth, uint[] color, int w, int h, CancellationToken cancellation)
    {
        for(var i = 0; i < vertices.Length; i++)
        {
            var p = vertices[i].Position;
            vertices[i] = vertices[i] with { Position = new(w * .5f + (p.X - center.X) * fit, h * .5f - (p.Y - center.Y) * fit, p.Z) };
        }
        for(var i = 0; i + RingSides < vertices.Length; i += RingSides)
        {
            cancellation.ThrowIfCancellationRequested();
            for(var j = 0; j < RingSides; j++)
            {
                var k = (j + 1) % RingSides;
                Triangle(vertices[i + j], vertices[i + k], vertices[i + j + RingSides]);
                Triangle(vertices[i + k], vertices[i + k + RingSides], vertices[i + j + RingSides]);
            }
        }
        return;

        void Triangle(Vertex va, Vertex vb, Vertex vc)
        {
            var a = va.Position;
            var b = vb.Position;
            var c = vc.Position;
            var area = Edge(a, b, c.X, c.Y);
            if(MathF.Abs(area) < .001f) return;
            var min = Vector3.Min(a, Vector3.Min(b, c));
            var max = Vector3.Max(a, Vector3.Max(b, c));
            var x0 = Math.Max(0, (int)MathF.Floor(min.X));
            var x1 = Math.Min(w - 1, (int)MathF.Ceiling(max.X));
            var y0 = Math.Max(0, (int)MathF.Floor(min.Y));
            var y1 = Math.Min(h - 1, (int)MathF.Ceiling(max.Y));
            var invArea = 1 / area;
            for(var y = y0; y <= y1; y++)
            for(var x = x0; x <= x1; x++)
            {
                var u = Edge(b, c, x + .5f, y + .5f) * invArea;
                var v = Edge(c, a, x + .5f, y + .5f) * invArea;
                var t = 1 - u - v;
                if(u < 0 || v < 0 || t < 0) continue;
                var z = u * a.Z + v * b.Z + t * c.Z;
                var k = y * w + x;
                if(z <= depth[k]) continue;
                depth[k] = z;
                color[k] = Paint(u * va.Lightness + v * vb.Lightness + t * vc.Lightness);
            }
        }

        static float Edge(Vector3 a, Vector3 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
    }

    private static byte[] Resolve(float[] depth, uint[] color, int w, int h, float fit, CancellationToken cancellation)
    {
        var rgba = new byte[w * h];
        var edgeStep = Math.Clamp(2.5f / fit, .012f, .040f);
        for(var y = 0; y < h; y += 2)
        {
            if((y & 31) == 0) cancellation.ThrowIfCancellationRequested();
            for(var x = 0; x < w; x += 2)
            {
                int r = 0, g = 0, b = 0, n = 0;
                for(var dy = 0; dy < 2; dy++)
                for(var dx = 0; dx < 2; dx++)
                {
                    var px = x + dx;
                    var py = y + dy;
                    var j = py * w + px;
                    var c = color[j];
                    if(c == 0) continue;
                    var z = depth[j];
                    if(px == 0 || px == w - 1 || py == 0 || py == h - 1
                        || color[j - 1] == 0 || color[j + 1] == 0 || color[j - w] == 0 || color[j + w] == 0)
                        c = 0x6F5443; // silhouette, inside coverage to keep the card clean
                    else if(z - depth[j - 1] > edgeStep || z - depth[j + 1] > edgeStep
                        || z - depth[j - w] > edgeStep || z - depth[j + w] > edgeStep)
                        c = 0x967C65;
                    r += (int)(c & 255); g += (int)((c >> 8) & 255); b += (int)(c >> 16); n++;
                }
                if(n == 0) continue;
                var i = y * w + x * 2;
                rgba[i] = (byte)(r / n);
                rgba[i + 1] = (byte)(g / n);
                rgba[i + 2] = (byte)(b / n);
                rgba[i + 3] = (byte)((n * 255 + 2) / 4);
            }
        }
        return rgba;
    }

    private static List<Form>? BuildForms(LibraryPoseDocument pose, out Ring[] torso)
    {
        torso = [];
        bool TryPoint(string name, out Vector3 point)
        {
            point = default;
            if(!pose.Bones.TryGetValue(name, out var bone) || !PoseGeometry.IsFinite(bone)) return false;
            point = bone.Position;
            return true;
        }
        bool TrySide(string s, out Side side)
        {
            side = default;
            if(!TryPoint("j_ude_a_" + s, out var shoulder) || !TryPoint("j_ude_b_" + s, out var elbow)
                || !TryPoint("j_te_" + s, out var wrist) || !TryPoint("j_asi_a_" + s, out var hip)
                || !TryPoint("j_asi_b_" + s, out var knee) || !TryPoint("j_asi_d_" + s, out var ankle))
                return false;
            side = new(shoulder, elbow, wrist, hip, knee, ankle);
            return true;
        }

        if(!TryPoint("j_kosi", out var waist) || !TryPoint("j_sebo_b", out var mid)
            || !TryPoint("j_sebo_c", out var chest) || !TryPoint("j_kubi", out var neck)
            || !TrySide("l", out var left) || !TrySide("r", out var right))
            return null;

        var shoulders = left.Shoulder - right.Shoulder;
        var hips = left.Hip - right.Hip;
        var hipCenter = (left.Hip + right.Hip) * .5f;
        var breadth = shoulders.Length();
        if(!Usable(breadth) || !Usable(hips.Length()) || !Usable(Vector3.Distance(hipCenter, neck))
            || !Usable(Vector3.Distance(waist, mid)) || !Usable(Vector3.Distance(mid, chest))
            || !Usable(Vector3.Distance(chest, neck))
            || !GoodSide(left) || !GoodSide(right))
            return null;

        var forms = new List<Form>(24);
        var unit = Math.Clamp(Math.Max(breadth, hips.Length() * 1.35f), .14f, .65f);
        var up = Direction(neck - chest, Direction(chest - waist, Vector3.UnitY));
        var lowerUp = Direction(waist - hipCenter, Direction(mid - waist, up));
        var pelvisWidth = Math.Clamp(hips.Length() * .81f, unit * .36f, unit * .49f);
        torso =
        [
            new(hipCenter - lowerUp * (unit * .15f), hips, .01f * unit, .01f * unit),
            new(hipCenter - lowerUp * (unit * .08f), hips, pelvisWidth * .75f, .22f * unit),
            new(Vector3.Lerp(hipCenter, waist, .38f), hips, pelvisWidth, .30f * unit),
            new(waist, Vector3.Lerp(hips, shoulders, .35f), .38f * unit, .266f * unit),
            new(mid, Vector3.Lerp(hips, shoulders, .70f), .365f * unit, .247f * unit),
            new(chest, shoulders, .452f * unit, .288f * unit),
            new(Vector3.Lerp(chest, neck, .49f), shoulders, .454f * unit, .262f * unit),
            new(Vector3.Lerp(chest, neck, .74f), shoulders, .33f * unit, .196f * unit),
            new(neck, shoulders, .139f * unit, .144f * unit),
            new(neck + up * (unit * .045f), shoulders, .015f * unit, .015f * unit),
        ];

        var headUp = up;
        var headCenter = neck + up * (unit * .44f);
        if(TryPoint("j_kao", out var face) && Vector3.DistanceSquared(face, neck) < unit * unit)
        {
            // Head joint is near the base/front of the head, not its center.
            var headRotation = pose.Bones["j_kao"].Rotation;
            headUp = Vector3.Transform(-Vector3.UnitY, Quaternion.Normalize(headRotation));
            headCenter = face + headUp * (unit * .17f)
                - Vector3.Transform(Vector3.UnitX, Quaternion.Normalize(headRotation)) * (unit * .075f);
        }
        Segment(neck - up * (unit * .07f), Vector3.Lerp(neck, headCenter, .70f), shoulders, unit * .135f, unit * .14f, .02f);
        Add(headCenter, headUp, shoulders, new(unit * .22f, unit * .305f, unit * .22f), .04f);
        AddSide(left, "l");
        AddSide(right, "r");
        return forms;

        static bool Usable(float length) => length is > .025f and < 5f;
        static bool GoodSide(Side s) => Usable(Vector3.Distance(s.Shoulder, s.Elbow)) && Usable(Vector3.Distance(s.Elbow, s.Wrist))
            && Usable(Vector3.Distance(s.Hip, s.Knee)) && Usable(Vector3.Distance(s.Knee, s.Ankle));
        void AddSide(Side s, string suffix)
        {
            Segment(s.Shoulder, s.Elbow, shoulders, unit * .144f, unit * .149f);
            Segment(s.Elbow, s.Wrist, shoulders, unit * .116f, unit * .117f, .012f, .43f);
            Segment(s.Hip, s.Knee, hips, unit * .225f, unit * .228f, -.023f, .43f);
            Segment(s.Knee, s.Ankle, hips, unit * .151f, unit * .163f, -.009f, .42f);
            Joint(s.Shoulder, .128f);
            Joint(s.Elbow, .087f);
            Joint(s.Knee, .108f);
            if(TryPoint("j_naka_b_" + suffix, out var fingers) && PlausibleEnd(s.Wrist, fingers))
            {
                var across = TryPoint("j_hito_a_" + suffix, out var index) && TryPoint("j_ko_a_" + suffix, out var little)
                    ? index - little : shoulders;
                Segment(s.Wrist, fingers, across, unit * .107f, unit * .067f, .032f);
            }
            if(TryPoint("j_asi_e_" + suffix, out var toe) && PlausibleEnd(s.Ankle, toe))
                Segment(s.Ankle, toe, hips, unit * .121f, unit * .092f, .012f, .62f);
        }
        void Joint(Vector3 center, float radius) =>
            Add(center, Vector3.UnitY, Vector3.UnitX, new(unit * radius), .012f);
        bool PlausibleEnd(Vector3 start, Vector3 end)
        {
            var distance = Vector3.DistanceSquared(start, end);
            return distance > .0001f && distance < unit * unit;
        }
        void Segment(Vector3 a, Vector3 b, Vector3 across, float width, float thick, float tone = 0, float bias = .5f)
        {
            var d = b - a;
            Add(Vector3.Lerp(a, b, bias), d, across,
                new(width, d.Length() * Math.Max(bias, 1 - bias) + Math.Min(width, thick) * .46f, thick), tone);
        }
        void Add(Vector3 center, Vector3 along, Vector3 across, Vector3 radii, float tone = 0)
        {
            Basis(along, across, out var x, out var y, out var z);
            forms.Add(new(center, x * radii.X, y * radii.Y, z * radii.Z, tone));
        }
    }

    private static void Basis(Vector3 along, Vector3 across, out Vector3 x, out Vector3 y, out Vector3 z)
    {
        y = Direction(along, Vector3.UnitY);
        x = Direction(across - y * Vector3.Dot(y, across),
            Direction(Vector3.Cross(y, Vector3.UnitZ), Vector3.UnitX));
        z = Vector3.Normalize(Vector3.Cross(x, y));
    }

    private static Vector3 Direction(Vector3 v, Vector3 fallback) => v.LengthSquared() > .0000001f ? Vector3.Normalize(v) : fallback;
}
