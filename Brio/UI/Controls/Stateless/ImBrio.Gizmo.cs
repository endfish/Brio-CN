using Brio.Config;
using Brio.Game.Camera;
using Brio.Input;
using Brio.UI.Controls.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System;
using System.Numerics;

namespace Brio.UI.Controls.Stateless;

public static partial class ImBrioGizmo
{
    const int numPoints = 144;
    const int axisHoverMouseDist = 10;

    private static readonly Style style = new();

    // One state per widget. ImGui owns the mouse capture; this state owns the
    // axis and target for that capture, including when the cursor leaves it.
    public sealed class RotationState
    {
        internal Axis? LockedAxis;
        internal Axis? DragAxis;
        internal Vector2 MouseStart, Direction, Marker;
        internal float Distance;
        private int _lastFrame = -1;
        private object?[] _targets = [];

        public bool IsUsing => DragAxis is not null;
        internal bool DrawnThisFrame => _lastFrame == ImGui.GetFrameCount();

        // Call before reading a cached matrix: never apply an old gesture to
        // a newly selected actor/bone, or resume it after missing its release.
        public bool Prepare(params object?[] targets)
        {
            var frame = ImGui.GetFrameCount();
            var invalidated = _lastFrame != frame && _lastFrame != frame - 1;
            _lastFrame = frame;
            invalidated |= _targets.Length != targets.Length;
            if(!invalidated)
                for(var i = 0; i < targets.Length; i++)
                    if(!Equals(_targets[i], targets[i]))
                    {
                        invalidated = true;
                        break;
                    }
            _targets = targets;
            if(IsUsing && (!ImGui.IsMouseDown(ImGuiMouseButton.Left)
                || ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.GetIO().AppFocusLost))
                invalidated = true;
            if(invalidated)
                Cancel();
            return invalidated;
        }

        public void Cancel()
        {
            DragAxis = null;
            Distance = 0;
        }
    }

    public unsafe static bool DrawRotation(ref Matrix4x4 matrix, Vector2 size, RotationState state, bool worldSpace = false)
    {
        // decompose the matrix
        if(!Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
        {
            state.Cancel();
            return false;
        }

        Quaternion editingRotation = worldSpace ? Quaternion.Identity : rotation;

        bool changed = DrawRotation(ref editingRotation, size, state);

        // recompose the matrix
        if(changed)
        {
            if(worldSpace)
            {
                matrix = Matrix4x4.CreateScale(scale);
                matrix = Matrix4x4.Transform(matrix, rotation);
                matrix = Matrix4x4.Transform(matrix, editingRotation);
                matrix.Translation = translation;
            }
            else
            {
                matrix = Matrix4x4.CreateScale(scale);
                matrix = Matrix4x4.Transform(matrix, editingRotation);
                matrix.Translation = translation;
            }
        }

        return changed;
    }

    private unsafe static bool DrawRotation(ref Quaternion rotation, Vector2 size, RotationState state)
    {
        bool changed = false;
        MouseData mouseData = new();

        BrioCamera* camera = (BrioCamera*)CameraManager.Instance()->GetActiveCamera();
        if(camera == null || !float.IsFinite(size.X) || !float.IsFinite(size.Y) || Math.Min(size.X, size.Y) <= 42)
        {
            state.Cancel();
            return false;
        }
        var viewMatrix = camera->GetViewMatrix();

        // extract just rotation from camera view
        {
            Matrix4x4.Decompose(viewMatrix, out var cameraScale, out var cameraRotation, out var cameraTranslation);
            viewMatrix = Matrix4x4.CreateFromQuaternion(cameraRotation);
        }

        Matrix4x4 mat = Matrix4x4.CreateScale(-1, 1, 1);
        viewMatrix *= mat;

        float radius = Math.Min(size.X / 2, size.Y / 2) - 20;
        int lineThickness = 2;

        // reset context
        mouseData.mousePos = ImGui.GetMousePos();
        mouseData.closestAxisPointToMouseDistance = float.MaxValue;
        mouseData.closestAxisMousePos = null;
        mouseData.closestAxisMouseFromPos = null;

        // create a transform matrix
        var transformMatrix = Matrix4x4.CreateFromQuaternion(rotation);
        transformMatrix.Translation = new Vector3(0, -5, 0);

        using(ImRaii.PushColor(ImGuiCol.ChildBg, UIConstants.Transparent))
        using(var child = ImRaii.Child("##imbriozmo", size))
        {
            if(child.Success)
            {
                var drawList = ImGui.GetWindowDrawList();
                Vector2 topPos = ImGui.GetWindowPos() + ImGui.GetWindowContentRegionMin();
                Vector2 botPos = ImGui.GetWindowPos() + ImGui.GetWindowContentRegionMax();
                bool isMouseOverArea = (mouseData.mousePos.X > topPos.X && mouseData.mousePos.Y > topPos.Y && mouseData.mousePos.X < botPos.X && mouseData.mousePos.Y < botPos.Y);

                Vector2 center = topPos + ((botPos - topPos) / 2);

                drawList.AddCircleFilled(center, radius, 0x50000000);

                DrawAxis(ref drawList, ref viewMatrix, ref transformMatrix, mouseData, center, lineThickness, radius, Axis.X, state);
                DrawAxis(ref drawList, ref viewMatrix, ref transformMatrix, mouseData, center, lineThickness, radius, Axis.Y, state);
                DrawAxis(ref drawList, ref viewMatrix, ref transformMatrix, mouseData, center, lineThickness, radius, Axis.Z, state);

                // Submit before checking input. ActiveId respects occlusion and
                // disabled UI, and holds the gesture outside the rings/window.
                ImGui.InvisibleButton("##imbriozmo_cover", botPos - topPos);
                var itemActive = ImGui.IsItemActive();
                var itemHovered = ImGui.IsItemHovered();
                var itemActivated = ImGui.IsItemActivated();
                if(state.IsUsing && (!itemActive || !ImGui.IsMouseDown(ImGuiMouseButton.Left)
                    || ImGui.GetIO().AppFocusLost || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)))
                    state.Cancel();

                var isAxisHovered = itemHovered && isMouseOverArea && !ImGui.GetIO().AppFocusLost
                    && mouseData.closestAxisMousePos is not null
                    && (mouseData.closestAxisPointToMouseDistance < axisHoverMouseDist || state.LockedAxis is not null);

                // Mouse drag
                if(state.DragAxis is { } dragAxis)
                {
                    drawList.AddCircleFilled(state.Marker, lineThickness * 2, style.AxisForegroundColors[(int)dragAxis]);
                    float newDragDistance = Vector2.Dot(mouseData.mousePos - state.MouseStart, state.Direction);
                    float dragDelta = newDragDistance - state.Distance;
                    state.Distance = newDragDistance;
                    if(float.IsFinite(dragDelta) && dragDelta != 0)
                    {
                        rotation *= AxisRotation(dragAxis, ModifyIncrement(dragDelta / 200));
                        changed = true;
                    }
                }

                // Mouse Hover
                else if(isAxisHovered && mouseData.closestAxisMousePos is Vector2 axisMousePosition)
                {
                    // A held button is not a new press on this axis. A press
                    // in the blank part of the sphere must not latch later.
                    if(itemActivated && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                        && mouseData.closestAxisMouseFromPos is { } from)
                    {
                        var tangent = axisMousePosition - from;
                        if(float.IsFinite(tangent.LengthSquared()) && tangent.LengthSquared() > 0.000001f)
                        {
                            state.DragAxis = mouseData.closestAxis;
                            state.MouseStart = mouseData.mousePos;
                            state.Direction = Vector2.Normalize(tangent);
                            state.Marker = axisMousePosition;
                            state.Distance = 0;
                        }
                    }
                    if(ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    {
                        if(state.LockedAxis == null)
                        {
                            state.LockedAxis = mouseData.closestAxis;
                        }
                        else
                        {
                            state.LockedAxis = null;
                        }
                    }
                    else if(!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    {
                        float mouseWheel = ImGui.GetIO().MouseWheel / 100;

                        if(float.IsFinite(mouseWheel) && mouseWheel != 0)
                        {
                            rotation *= AxisRotation(mouseData.closestAxis, ModifyIncrement(mouseWheel));
                            changed = true;
                        }
                    }

                    drawList.AddCircle(axisMousePosition, axisHoverMouseDist, style.AxisForegroundColors[(int)mouseData.closestAxis]);
                }
            }
            else
                state.Cancel();
        }

        return changed;
    }

    private static float ModifyIncrement(float increment)
    {
        if(InputManagerService.ActionKeysPressed(InputAction.Interface_IncrementSmallModifier))
            increment /= 10;
        if(InputManagerService.ActionKeysPressed(InputAction.Interface_IncrementLargeModifier))
            increment *= 10;
        return increment;
    }

    private static Quaternion AxisRotation(Axis axis, float angle) => axis switch
    {
        Axis.X => Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle),
        Axis.Y => Quaternion.CreateFromAxisAngle(Vector3.UnitY, -angle),
        _ => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle),
    };

    private unsafe static void DrawAxis(
        ref ImDrawListPtr drawList,
        ref Matrix4x4 viewMatrix,
        ref Matrix4x4 transformMatrix,
        MouseData mouseData,
        Vector2 center,
        int thickness,
        float radius,
        Axis axis,
        RotationState state)
    {
        Vector3[] points3d = new Vector3[numPoints];
        for(int i = 0; i < points3d.Length; i++)
        {
            float p = (float)i / (float)(points3d.Length - 1);
            float r = p * (MathF.PI * 2);

            if(axis == Axis.Z)
            {
                points3d[i] = new Vector3(radius * MathF.Cos(r), radius * MathF.Sin(r), 0);
            }
            else if(axis == Axis.X)
            {
                points3d[i] = new Vector3(0, radius * MathF.Cos(r), radius * MathF.Sin(r));
            }
            else if(axis == Axis.Y)
            {
                points3d[i] = new Vector3(radius * MathF.Cos(r), 0, radius * MathF.Sin(r));
            }
        }

        float zClip = 0f;
        for(int i = 1; i < points3d.Length; i++)
        {
            Vector3 fromPoint = Vector3.Transform(points3d[i - 1], transformMatrix);
            fromPoint = Vector3.Transform(fromPoint, viewMatrix);

            Vector3 toPoint = Vector3.Transform(points3d[i], transformMatrix);
            toPoint = Vector3.Transform(toPoint, viewMatrix);

            bool isVisible = toPoint.Z < zClip;

            Vector2 fromPos = center + new Vector2(fromPoint.X, fromPoint.Y);
            Vector2 toPos = center + new Vector2(toPoint.X, toPoint.Y);

            uint segmentColor = isVisible ? style.AxisForegroundColors[(int)axis] : style.AxisBackgroundColors[(int)axis];

            if(state.LockedAxis == axis)
                segmentColor = isVisible ? style.LockedAxisForegroundColor : style.LockedAxisBackgroundColor;

            // check mouse
            if(isVisible && (state.LockedAxis == null || state.LockedAxis == axis))
            {
                float distToMouse = Vector2.Distance(toPos, mouseData.mousePos);
                if(distToMouse <= mouseData.closestAxisPointToMouseDistance)
                {
                    mouseData.closestAxisPointToMouseDistance = distToMouse;
                    mouseData.closestAxisMousePos = toPos;
                    mouseData.closestAxisMouseFromPos = fromPos;
                    mouseData.closestAxis = axis;
                }
            }

            drawList.AddLine(fromPos, toPos, segmentColor, state.DragAxis == axis ? thickness + 1 : thickness);
        }
    }

    public enum Axis
    {
        X,
        Y,
        Z,
    }
    public struct Style
    {
        public uint LockedAxisForegroundColor = 0xFFFFFFFF;
        public uint LockedAxisBackgroundColor = 0x10FFFFFF;

        public uint[] AxisForegroundColors =
        [
            0xFF3333FF,
            0xFF33FF33,
            0xFFFF3333,
        ];

        public uint[] AxisBackgroundColors =
        [
            0x103333FF,
            0x1033FF33,
            0x10FF3333,
        ];

        public Style()
        {
        }
    }
    public class MouseData
    {
        public Vector2 mousePos;
        public Vector2? closestAxisMousePos;
        public Vector2? closestAxisMouseFromPos;
        public float closestAxisPointToMouseDistance;
        public Axis closestAxis;
    }
}
