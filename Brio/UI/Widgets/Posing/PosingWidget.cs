using Brio.Capabilities.Actor;
using Brio.Capabilities.Posing;
using Brio.Input;
using Brio.UI.Controls.Core;
using Brio.UI.Controls.Editors;
using Brio.UI.Controls.Stateless;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Brio.UI.Widgets.Posing;

public class PosingWidget(PosingCapability capability) : Widget<PosingCapability>(capability)
{
    public override string HeaderName => global::Brio.Resources.Localize.Get("ui.actor.posing", "Posing");

    public override WidgetFlags Flags => Capability.Actor.IsProp ? (WidgetFlags.DefaultOpen | WidgetFlags.DrawBody) : (WidgetFlags.DrawBody | WidgetFlags.HasAdvanced | WidgetFlags.DefaultOpen);

    private readonly PosingTransformEditor _posingTransformEditor = new();

    private readonly BoneSearchControl _boneSearchEditor = new();


    public override void DrawBody()
    {
        DrawButtons();

        using var child1 = ImRaii.Child($"###appearance_child", new Vector2(0, 165 * ImGuiHelpers.GlobalScale), true, ImGuiWindowFlags.AlwaysAutoResize);
        if(child1.Success)
        {
            DrawTransform();
        }
    }

    private void DrawButtons()
    {
        if(Capability.Actor.TryGetCapability<ActionTimelineCapability>(out var timelineCapability) == false)
        {
            return;
        }

        var overlayOpen = Capability.OverlayOpen;
        if(ImBrio.FontIconButton("overlay", overlayOpen ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye, overlayOpen ? global::Brio.Resources.Localize.Get("ui.actor.closeOverlay", "Close Overlay") : global::Brio.Resources.Localize.Get("ui.actor.openOverlay", "Open Overlay")))
        {
            Capability.OverlayOpen = !overlayOpen;
        }

        ImGui.SameLine();

        if(Capability.Actor.IsProp == false)
        {
            if(ImBrio.FontIconButton("import", FontAwesomeIcon.FileDownload, global::Brio.Resources.Localize.Get("ui.actor.importPose", "Import Pose")))
            {
                ImGui.OpenPopup("DrawImportPoseMenuPopup");
            }

            FileUIHelpers.DrawImportPoseMenuPopup("postingWidget", Capability);

            ImGui.SameLine();

            if(ImBrio.FontIconButton("export", FontAwesomeIcon.Save, global::Brio.Resources.Localize.Get("ui.actor.savePose", "Save Pose")))
                FileUIHelpers.ShowExportPoseModal(Capability);

            ImGui.SameLine();

            if(ImBrio.FontIconButton("bone_search", FontAwesomeIcon.Search, global::Brio.Resources.Localize.Get("ui.actor.boneSearch", "Bone Search")))
            {
                ImGui.OpenPopup("widget_bone_search_popup");
            }
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButton("undo", FontAwesomeIcon.Backward, global::Brio.Resources.Localize.Get("ui.actor.undo", "Undo"), Capability.CanUndo) || (InputManagerService.ActionKeysPressedLastFrame(InputAction.Posing_Undo) && Capability.CanUndo))
        {
            Capability.Undo();
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButton("redo", FontAwesomeIcon.Forward, global::Brio.Resources.Localize.Get("ui.actor.redo", "Redo"), Capability.CanRedo) || (InputManagerService.ActionKeysPressedLastFrame(InputAction.Posing_Redo) && Capability.CanRedo))
        {
            Capability.Redo();
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButton("flipButton", FontAwesomeIcon.Repeat, global::Brio.Resources.Localize.Get("ui.actor.mirrorPose", "Mirror Pose")))
        {
            Capability.MirrorPose();
        }

        ImGui.SameLine();

        if(Capability.Actor.IsProp == false)
        {
            if(ImBrio.ToggelFontIconButton("freezeActor", FontAwesomeIcon.Snowflake, new Vector2(0), timelineCapability.SpeedMultiplier == 0, hoverText: timelineCapability.SpeedMultiplierOverride == 0 ? global::Brio.Resources.Localize.Get("ui.actor.unfreezeCharacter", "Un-Freeze Character") : global::Brio.Resources.Localize.Get("ui.actor.freezeCharacter", "Freeze Character")) || InputManagerService.ActionKeysPressedLastFrame(InputAction.Posing_Freeze))
            {
                if(timelineCapability.SpeedMultiplierOverride == 0)
                    timelineCapability.ResetOverallSpeedOverride();
                else
                    timelineCapability.SetOverallSpeedOverride(0f);
            }
            ImGui.SameLine();
        }

        if(ImBrio.FontIconButtonRight("reset", FontAwesomeIcon.Undo, 1, global::Brio.Resources.Localize.Get("ui.actor.resetPose", "Reset Pose"), Capability.HasOverride()))
        {
            ImGui.OpenPopup("widget_reset_pose_popup");
        }

        using(var popup = ImRaii.Popup("widget_reset_pose_popup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            if(popup.Success)
            {
                DrawResetMenu();
            }
        }

        using(var popup = ImRaii.Popup("widget_bone_search_popup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            if(popup.Success)
            {
                _boneSearchEditor.Draw("widget_bone_search", Capability);
            }
        }
    }

    private void DrawTransform()
    {
        PosingEditorCommon.DrawSelectionName(Capability);

        _posingTransformEditor.Draw("posing_widget_transform", Capability, true);
    }

    private void DrawResetMenu()
    {
        using(ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0, 0.5f)))
        using(ImRaii.PushColor(ImGuiCol.Button, UIConstants.Transparent))
        {
            {
                var buttonSize = new Vector2(155 * ImGuiHelpers.GlobalScale, 0);
                if(ImBrio.DrawIconButton(FontAwesomeIcon.Undo, global::Brio.Resources.Localize.Get("ui.actor.resetPose", "Reset Pose"), buttonSize))
                {
                    Capability.Reset(false, false);
                    ImGui.CloseCurrentPopup();
                }

                using(ImRaii.Disabled(!Capability.HasOverride(Capability.SkeletonPosing.FilterNonFaceBones)))
                {
                    if(ImBrio.DrawIconButton(FontAwesomeIcon.ChildReaching, global::Brio.Resources.Localize.Get("ui.actor.resetBody", "Reset Body"), buttonSize))
                    {
                        Capability.Snapshot(false, reconcile: false);
                        Capability.SkeletonPosing.PoseInfo.Clear(Capability.SkeletonPosing.FilterNonFaceBones);
                        ImGui.CloseCurrentPopup();
                    }
                }

                using(ImRaii.Disabled(!Capability.HasOverride(Capability.SkeletonPosing.FilterFaceBones)))
                {
                    if(ImBrio.DrawIconButton(FontAwesomeIcon.Smile, global::Brio.Resources.Localize.Get("ui.actor.resetFace", "Reset Face"), buttonSize))
                    {
                        Capability.SkeletonPosing.PoseInfo.Clear(Capability.SkeletonPosing.FilterFaceBones);
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
        }
    }

    public override void ToggleAdvancedWindow()
    {
        UIManager.Instance.ToggleGraphicalPosingWindow();
    }
}
