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
using System;
using System.Numerics;

namespace Brio.UI.Widgets.Posing;

public class PosingWidget(PosingCapability capability) : Widget<PosingCapability>(capability)
{
    public override string HeaderName => "Posing";

    public override WidgetFlags Flags => (WidgetFlags.DrawBody | WidgetFlags.HasAdvanced | WidgetFlags.DefaultOpen);

    private readonly PosingTransformEditor _posingTransformEditor = new();


    public override void DrawBody()
    {
        DrawButtons();

        using(ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f))
        using(var child1 = ImRaii.Child($"###appearance_child", new Vector2(0, 165 * ImGuiHelpers.GlobalScale), true, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if(child1.Success)
            {
                DrawTransform();
            }
        }
    }

    private void DrawButtons()
    {
        if(Capability.Actor.TryGetCapability<ActionTimelineCapability>(out var timelineCapability) == false)
        {
            DrawPoseManagerButton(new Vector2(25));
            return;
        }

        // Nine icons and three separators. Keep the file group together;
        // move the following tools to a new row if scaling leaves no room.
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var fittingWidth = (ImGui.GetContentRegionAvail().X - 11 * spacing - 9 * scale) / (9 * scale);
        var wrapTools = fittingWidth < 22;
        var buttonSize = new Vector2(wrapTools ? 25 : MathF.Min(25, MathF.Floor(fittingWidth)), 25);

        var overlayOpen = Capability.OverlayOpen;
        if(ImBrio.FontIconButton("overlay", overlayOpen ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye,
            global::Brio.Resources.Localize.Text(overlayOpen ? "Close Overlay" : "Open Overlay"), size: buttonSize))
        {
            Capability.OverlayOpen = !overlayOpen;
        }

        ImBrio.VerticalSeparator(24, 1);

        if(ImBrio.FontIconButton("import", FontAwesomeIcon.FileDownload, global::Brio.Resources.Localize.Text("Import Pose"), size: buttonSize))
        {
            ImGui.OpenPopup("DrawImportPoseMenuPopup");
        }

        FileUIHelpers.DrawImportPoseMenuPopup("postingWidget", Capability);

        ImGui.SameLine();

        if(ImBrio.FontIconButton("export", FontAwesomeIcon.Save, global::Brio.Resources.Localize.Text("Save Pose"), size: buttonSize))
            ImGui.OpenPopup("DrawExportPoseMenuPopup");

        FileUIHelpers.DrawExportPoseMenuPopup(Capability);

        ImGui.SameLine();
        DrawPoseManagerButton(buttonSize);

        if(wrapTools)
            ImGui.Spacing();
        else
            ImBrio.VerticalSeparator(24, 1);

        if(ImBrio.FontIconButton("undo", FontAwesomeIcon.Reply, global::Brio.Resources.Localize.Text("Undo"), Capability.CanUndo, size: buttonSize) || (InputManagerService.ActionKeysPressedLastFrame(InputAction.Posing_Undo) && Capability.CanUndo))
        {
            Capability.Undo();
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButton("redo", FontAwesomeIcon.Share, global::Brio.Resources.Localize.Text("Redo"), Capability.CanRedo, size: buttonSize) || (InputManagerService.ActionKeysPressedLastFrame(InputAction.Posing_Redo) && Capability.CanRedo))
        {
            Capability.Redo();
        }

        ImBrio.VerticalSeparator(24, 1);

        if(ImBrio.FontIconButton("flipButton", FontAwesomeIcon.Repeat, global::Brio.Resources.Localize.Text("Mirror Pose"), size: buttonSize))
        {
            Capability.MirrorPose();
        }

        ImGui.SameLine();

        if(ImBrio.ToggelFontIconButton("freezeActor", FontAwesomeIcon.Snowflake, new Vector2(buttonSize.X - 25, 0), timelineCapability.SpeedMultiplier == 0,
            tooltip: global::Brio.Resources.Localize.Text(timelineCapability.SpeedMultiplierOverride == 0 ? "Un-Freeze Character" : "Freeze Character")) || InputManagerService.ActionKeysPressedLastFrame(InputAction.Posing_Freeze))
        {
            if(timelineCapability.SpeedMultiplierOverride == 0)
                timelineCapability.ResetOverallSpeedOverride();
            else
                timelineCapability.SetOverallSpeedOverride(0f);
        }
        ImGui.SameLine();

        if(ImBrio.FontIconButtonRight("reset", FontAwesomeIcon.Undo, 1, global::Brio.Resources.Localize.Text("Reset Pose"), Capability.HasOverride(), size: buttonSize))
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
    }

    private static void DrawPoseManagerButton(Vector2 size)
    {
        if(ImBrio.FontIconButton("pose_manager", FontAwesomeIcon.BookBookmark, "Pose Manager", size: size))
            UIManager.Instance.TogglePoseManagerWindow();
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
                if(ImBrio.IconButtonWithText(FontAwesomeIcon.Undo, global::Brio.Resources.Localize.Text("Reset Pose"), buttonSize))
                {
                    Capability.Reset(false, false);
                    ImGui.CloseCurrentPopup();
                }

                using(ImRaii.Disabled(!Capability.HasOverride(Capability.SkeletonPosing.FilterNonFaceBones)))
                {
                    if(ImBrio.IconButtonWithText(FontAwesomeIcon.ChildReaching, global::Brio.Resources.Localize.Text("Reset Body"), buttonSize))
                    {
                        Capability.Snapshot(false, reconcile: false);
                        Capability.SkeletonPosing.PoseInfo.Clear(Capability.SkeletonPosing.FilterNonFaceBones);
                        ImGui.CloseCurrentPopup();
                    }
                }

                using(ImRaii.Disabled(!Capability.HasOverride(Capability.SkeletonPosing.FilterFaceBones)))
                {
                    if(ImBrio.IconButtonWithText(FontAwesomeIcon.Smile, global::Brio.Resources.Localize.Text("Reset Face"), buttonSize))
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
