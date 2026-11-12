using Brio.Capabilities.Posing;
using Brio.Entities;
using Brio.Game.Posing;
using Brio.Resources;
using Brio.Services.Poses;
using Brio.UI.Controls.Stateless;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brio.UI.Windows.Specialized;

public sealed class ExpressionEditorWindow(FaceEditingService editor, EntityManager entities, PoseManagerWindow poses)
    : Window(Localize.Text("Expression Editor###brio_expression_editor"))
{
    private bool _linked = true;
    private bool _wideRange;
    private bool _captureFailed;
    private int _sliderScope;
    private FaceEditingService.Target? _target;
    private IReadOnlySet<FaceControl> _available = new HashSet<FaceControl>();
    private readonly PosePreviewView _view = new();
    private static readonly string[] Labels = ["Jaw opening", "Mouth corners", "Pucker", "Upper lip", "Eyelids", "Squint", "Brow height", "Inner brow", "Tongue extension"];
    public override void PreDraw()
    {
        WindowName = Localize.Text("Expression Editor###brio_expression_editor");
        ImGui.SetNextWindowSize(new(460, 700), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new(390, 400), new(900, 1400));
        base.PreDraw();
    }

    public override void Draw()
    {
        if(!editor.IsBound && editor.Rig is not null) editor.End();
        var target = editor.GetTarget();
        if(_target != target)
        {
            _target = target;
            _sliderScope++;
            _captureFailed = false;
            _view.Reset();
            if(target is { } next) _available = FaceEditingService.DetectControls(next.Skeleton);
        }
        ImGui.TextUnformatted(entities.SelectedEntity?.FriendlyName ?? Localize.Text("No actor selected"));
        if(target is not { } ready || _available.Count == 0)
        {
            ImGui.TextWrapped(Localize.Text("Select a ready GPose humanoid with Dawntrail facial bones."));
            if(ImGui.Button(Localize.Text("Open advanced bone editor###face_advanced_empty"))) UIManager.Instance.ToggleGraphicalPosingWindow();
            return;
        }

        ImGui.TextWrapped(Localize.Text("Adjust a slider to start from the current expression. Zero restores that reference. Editing does not pause the actor."));
        var pending = ready.Capability.SkeletonPosing.HasTransitiveActions;
        if(ImGui.Button(Localize.Text("Freeze current frame###face_freeze"))) editor.FreezeCurrentFrame();
        ImGui.SameLine();
        using(ImRaii.Disabled(editor.Rig is null || pending))
            if(ImGui.Button(Localize.Text("Use current expression as reference###face_capture")))
            {
                _captureFailed = !editor.Capture();
                if(!_captureFailed) _view.Reset();
            }

        var values = editor.Values.Copy();
        var changed = false;
        var commit = false;
        ImGui.Checkbox(Localize.Text("Link left and right###face_link"), ref _linked);
        ImGui.SameLine();
        ImGui.Checkbox(Localize.Text("Wider range###face_wider_range"), ref _wideRange);
        var controls = editor.Rig?.Available ?? _available;
        // A held mouse button on the old actor must not drive a slider on the
        // new actor, even when both windows contain the same control.
        using(ImRaii.PushId(_sliderScope))
        using(ImRaii.Disabled(editor.Rig is null && pending))
        for(var i = 0; i < Labels.Length; i++)
        {
            var control = (FaceControl)i;
            if(!controls.Contains(control)) continue;
            ImGui.PushID(i);
            ImGui.TextUnformatted(Localize.Text(Labels[i]));
            ImGui.SetNextItemWidth(-1);
            if(_linked || control is FaceControl.JawOpening or FaceControl.TongueExtension)
            {
                var v = (values.Left[i] + values.Right[i]) / 2;
                if(DrawSlider("###both", control, ref v))
                {
                    values.Left[i] = values.Right[i] = v; changed = true;
                }
                commit |= ImGui.IsItemDeactivatedAfterEdit();
            }
            else
            {
                var width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
                ImGui.BeginGroup();
                ImGui.TextUnformatted(Localize.Text("Left"));
                ImGui.SetNextItemWidth(width);
                changed |= DrawSlider("###left", control, ref values.Left[i]);
                commit |= ImGui.IsItemDeactivatedAfterEdit();
                ImGui.EndGroup();
                ImGui.SameLine(); ImGui.BeginGroup();
                ImGui.TextUnformatted(Localize.Text("Right"));
                ImGui.SetNextItemWidth(width);
                changed |= DrawSlider("###right", control, ref values.Right[i]);
                commit |= ImGui.IsItemDeactivatedAfterEdit();
                ImGui.EndGroup();
            }
            ImGui.PopID();
        }
        if(changed) _captureFailed = !editor.Apply(values);
        if(commit) editor.Commit();
        if(_captureFailed)
            ImGui.TextWrapped(Localize.Text("The current facial frame is not ready. Try again once the actor is visible and posed."));

        using(ImRaii.Disabled(editor.Rig is null))
            if(ImGui.Button(Localize.Text("Restore captured expression###face_restore"))) { editor.Apply(new()); editor.Commit(); }
        ImGui.SameLine();
        if(ImGui.Button(Localize.Text("Save expression###face_save"))) { editor.Commit(); poses.RequestSave(PoseAssetKind.Expression); }
        if(entities.TryGetCapabilityFromSelectedEntity<PosingCapability>(out var cap, considerParents: false))
        {
            using(ImRaii.Disabled(!cap.CanUndo))
                if(ImGui.Button(Localize.Text("Undo###face_undo"))) { editor.End(); cap.Undo(); }
            ImGui.SameLine();
        }
        if(ImGui.Button(Localize.Text("Open advanced bone editor###face_advanced")))
        {
            editor.End(); UIManager.Instance.ToggleGraphicalPosingWindow();
        }
        if(editor.Rig is not { } rig) return;
        var preview = new LibraryPoseDocument();
        foreach(var (id, transform) in rig.Solve(editor.Values)) preview.Bones[id.BoneName] = transform;
        var metadata = new PoseAssetMetadata { Kind = PoseAssetKind.Expression };
        foreach(var bone in rig.Bones)
            if(bone.Parent is { } parent) metadata.Parents[bone.Id.BoneName] = parent.BoneName;
        PoseMannequin.Draw("###face_wireframe", preview, metadata, new(ImGui.GetContentRegionAvail().X, 150), _view);
        ImGui.TextDisabled(Localize.Text("Wireframe preview only. Check the actual expression on the scene actor."));
    }

    private bool DrawSlider(string id, FaceControl control, ref float value)
    {
        // Switching back to fine range must not clamp the existing pose.
        var max = Math.Max(_wideRange ? FaceControlValues.Maximum(control) : 1f, value);
        var min = FaceControlValues.IsOneSided(control) ? 0f : Math.Min(-(_wideRange ? FaceControlValues.Maximum(control) : 1f), value);
        return ImGui.SliderFloat(id, ref value, min, max, "%.2f", ImGuiSliderFlags.AlwaysClamp);
    }

    public override void OnClose()
    {
        editor.End();
        _target = null;
        base.OnClose();
    }
}
