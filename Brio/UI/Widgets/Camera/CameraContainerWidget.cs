using Brio.Capabilities.Camera;
using Brio.Entities.Camera;
using Brio.UI.Controls.Editors;
using Brio.UI.Controls.Stateless;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Brio.UI.Widgets.Camera;

public class CameraContainerWidget(CameraContainerCapability capability) : Widget<CameraContainerCapability>(capability)
{
    public override string HeaderName => global::Brio.Resources.Localize.Get("ui.entities.cameras", "Cameras");

    public override WidgetFlags Flags => WidgetFlags.DefaultOpen | WidgetFlags.DrawBody | WidgetFlags.DrawPopup | WidgetFlags.DrawQuickIcons;

    private CameraEntity? _selectedEntity;

    public override void DrawQuickIcons()
    {
        using(ImRaii.Disabled(Capability.IsAllowed == false))
        {
            bool hasSelection = _selectedEntity != null;

            if(ImBrio.FontIconButton("CameraContainerWidget_New_Camera", FontAwesomeIcon.Plus, global::Brio.Resources.Localize.Get("ui.camera.newCamera", "New Camera")))
            {
                ImGui.OpenPopup("DrawSpawnMenuPopup");
            }
            CameraEditor.DrawSpawnMenu(Capability.VirtualCameraManager);

            ImGui.SameLine();

            using(ImRaii.Disabled(hasSelection == false))
            {
                using(ImRaii.Disabled(_selectedEntity?.VirtualCamera.CameraID == null))
                {
                    if(ImBrio.FontIconButton("CameraLifetime_clone", FontAwesomeIcon.Clone, global::Brio.Resources.Localize.Get("ui.camera.cloneCamera", "Clone Camera")))
                    {
                        Capability.VirtualCameraManager.CloneCamera(_selectedEntity!.VirtualCamera.CameraID);
                    }
                }

                ImGui.SameLine();

                using(ImRaii.Disabled(_selectedEntity?.VirtualCamera.CameraID == 0))
                {
                    if(ImBrio.FontIconButton("CameraLifetime_destroy", FontAwesomeIcon.Trash, global::Brio.Resources.Localize.Get("ui.camera.destroyCamera", "Destroy Camera")))
                    {
                        Capability.VirtualCameraManager.DestroyCamera(_selectedEntity!.VirtualCamera.CameraID);
                    }
                }

                ImGui.SameLine();

                if(ImBrio.FontIconButton("CameraLifetime_target", FontAwesomeIcon.LocationCrosshairs, global::Brio.Resources.Localize.Get("ui.camera.targetCamera", "Target Camera")))
                {
                    Capability.VirtualCameraManager.SelectCamera(_selectedEntity!.VirtualCamera);
                }

                ImGui.SameLine();

                if(ImBrio.FontIconButton("containerwidget_selectinhierarchy", FontAwesomeIcon.FolderTree, global::Brio.Resources.Localize.Get("ui.actor.selectInHierarchy", "Select in Hierarchy"), hasSelection))
                {
                    Capability.VirtualCameraManager.SelectInHierarchy(_selectedEntity!);
                }
            }

            using(ImRaii.Disabled(Capability.VirtualCameraManager.CamerasCount == 0))
            {
                ImGui.SameLine();

                if(ImBrio.FontIconButton("containerwidget_destroyall", FontAwesomeIcon.Bomb, global::Brio.Resources.Localize.Get("ui.common.destroyAll", "Destroy All")))
                {
                    Capability.VirtualCameraManager.DestroyAll();
                }
            }
        }
    }

    public override void DrawPopup()
    {
        using(ImRaii.Disabled(Capability.IsAllowed == false))
        {
            if(ImGui.MenuItem($"{global::Brio.Resources.Localize.Get("ui.camera.openCameraEditor", "Open Camera Editor")}###containerwidgetpopup_OpenAdvance"))
            {
                Capability.OpenCameraWindow();
            }

            if(ImGui.BeginMenu($"{global::Brio.Resources.Localize.Get("ui.common.new", "New...")}###containerwidgetpopup_new"))
            {
                if(ImGui.MenuItem($"{global::Brio.Resources.Localize.Get("ui.camera.newCamera", "New Camera")}###containerwidgetpopup_newcamera"))
                {
                    Capability.VirtualCameraManager.CreateCamera(CameraType.Game);
                }
                if(ImGui.MenuItem($"{global::Brio.Resources.Localize.Get("ui.camera.newFreeCam", "New Free-Cam")}###containerwidgetpopup_newfreecamera"))
                {
                    Capability.VirtualCameraManager.CreateCamera(CameraType.Free);
                }

                ImGui.EndMenu();
            }

            if(ImGui.BeginMenu($"{global::Brio.Resources.Localize.Get("ui.camera.destroyAllCameras", "Destroy All Cameras")}###containerwidgetpopup_destroyall"))
            {
                if(ImGui.MenuItem($"{global::Brio.Resources.Localize.Get("ui.common.confirmDestruction", "Confirm Destruction")}###containerwidgetpopup_destroyall_confirm"))
                {
                    Capability.VirtualCameraManager.DestroyAll();
                }

                ImGui.EndMenu();
            }
        }
    }

    public unsafe override void DrawBody()
    {
        using(ImRaii.Disabled(Capability.IsAllowed == false))
        {
            if(ImGui.BeginListBox($"###CameraContainerWidget_{Capability.Entity.Id}_list", new Vector2(-1, 150 * ImGuiHelpers.GlobalScale)))
            {
                foreach(var child in Capability.Entity.Children)
                {
                    if(child is CameraEntity cameraEntity)
                    {
                        bool isSelected = cameraEntity.Equals(_selectedEntity);
                        if(ImGui.Selectable($"{child.FriendlyName}###CameraContainerWidget_{Capability.Entity.Id}_item_{cameraEntity.Id}", isSelected, ImGuiSelectableFlags.AllowDoubleClick))
                        {
                            _selectedEntity = cameraEntity;
                        }
                    }
                }

                ImGui.EndListBox();
            }
        }
    }
}
