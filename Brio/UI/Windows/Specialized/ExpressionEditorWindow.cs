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
using System.Linq;
using System.Numerics;

namespace Brio.UI.Windows.Specialized;

public sealed class ExpressionEditorWindow(FaceEditingService editor, EntityManager entities, PoseManagerWindow poses)
    : Window(Localize.Text("Expression Editor###brio_expression_editor"))
{
    private bool _linked = true;
    private readonly PosePreviewView _view = new();
    private static readonly string[] Labels = ["Jaw opening", "Mouth corners", "Pucker", "Upper lip", "Eyelids", "Squint", "Brow height", "Inner brow"];
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
        ImGui.TextUnformatted(entities.SelectedEntity?.FriendlyName ?? Localize.Text("No actor selected"));
        ImGui.TextWrapped(Localize.Text("First version: small geometric adjustments based on this face's proportions. Custom faces may need fine tuning in the bone editor."));
        if(ImGui.Button(Localize.Text("Capture current expression (freeze actor)###face_capture")))
        {
            if(!editor.Capture()) Brio.NotifyError(Localize.Text("Could not capture this face. Select a ready GPose humanoid with Dawntrail facial bones."));
            else _view.Reset();
        }
        if(!editor.IsBound || editor.Rig is not { } rig)
        {
            ImGui.TextWrapped(Localize.Text("Capture a reference expression first. Zero restores that reference, not the game's neutral face. The body pose is not replaced."));
            if(ImGui.Button(Localize.Text("Open advanced bone editor###face_advanced_empty"))) UIManager.Instance.ToggleGraphicalPosingWindow();
            return;
        }
        var values = editor.Values.Copy();
        var changed = false;
        ImGui.Checkbox(Localize.Text("Link left and right###face_link"), ref _linked);
        for(var i = 0; i < Labels.Length; i++)
        {
            var control = (FaceControl)i;
            if(!rig.Available.Contains(control)) continue;
            var min = control is FaceControl.JawOpening or FaceControl.Pucker or FaceControl.Squint ? 0 : -1;
            ImGui.PushID(i);
            ImGui.TextUnformatted(Localize.Text(Labels[i]));
            ImGui.SetNextItemWidth(-1);
            if(_linked || control == FaceControl.JawOpening)
            {
                var v = (values.Left[i] + values.Right[i]) / 2;
                if(ImGui.SliderFloat("###both", ref v, min, 1, "%.2f"))
                {
                    values.Left[i] = values.Right[i] = v; changed = true;
                }
                if(ImGui.IsItemDeactivatedAfterEdit()) editor.Commit();
            }
            else
            {
                var width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
                ImGui.BeginGroup();
                ImGui.TextUnformatted(Localize.Text("Left"));
                ImGui.SetNextItemWidth(width);
                changed |= ImGui.SliderFloat("###left", ref values.Left[i], min, 1, "%.2f");
                if(ImGui.IsItemDeactivatedAfterEdit()) editor.Commit();
                ImGui.EndGroup();
                ImGui.SameLine(); ImGui.BeginGroup();
                ImGui.TextUnformatted(Localize.Text("Right"));
                ImGui.SetNextItemWidth(width);
                changed |= ImGui.SliderFloat("###right", ref values.Right[i], min, 1, "%.2f");
                if(ImGui.IsItemDeactivatedAfterEdit()) editor.Commit();
                ImGui.EndGroup();
            }
            ImGui.PopID();
        }
        if(changed) editor.Apply(values);
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
        var preview = new LibraryPoseDocument();
        foreach(var (id, transform) in rig.Solve(editor.Values)) preview.Bones[id.BoneName] = transform;
        var metadata = new PoseAssetMetadata { Kind = PoseAssetKind.Expression };
        foreach(var bone in rig.Bones)
            if(bone.Parent is { } parent) metadata.Parents[bone.Id.BoneName] = parent.BoneName;
        PoseMannequin.Draw("###face_wireframe", preview, metadata, new(ImGui.GetContentRegionAvail().X, 150), _view);
        ImGui.TextDisabled(Localize.Text("Wireframe preview only. Check the actual expression on the scene actor."));
    }

    public override void OnClose() { editor.End(); base.OnClose(); }
}
