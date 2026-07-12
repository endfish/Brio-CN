using Brio.Capabilities.Camera;
using Brio.UI.Controls.Stateless;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Brio.UI.Widgets.Camera;

public class CameraLifetimeWidget(CameraLifetimeCapability capability) : Widget<CameraLifetimeCapability>(capability)
{
    public override string HeaderName => "Lifetime";

    public override WidgetFlags Flags => WidgetFlags.DrawPopup | WidgetFlags.DrawQuickIcons;

    public override void DrawQuickIcons()
    {
        using(ImRaii.Disabled(Capability.IsAllowed == false))
        {
            if(ImBrio.FontIconButton("CameraLifetime_clone", FontAwesomeIcon.Clone, global::Brio.Resources.Localize.Text("Clone Camera")))
            {
                Capability.VirtualCameraManager.CloneCamera(Capability.CameraEntity.CameraID);
            }

            ImGui.SameLine();

            if(ImBrio.FontIconButton("CameraLifetime_target", FontAwesomeIcon.LocationCrosshairs, global::Brio.Resources.Localize.Text("Set as Active Camera")))
            {
                Capability.VirtualCameraManager.SelectCamera(Capability.VirtualCamera);
            }

            ImBrio.VerticalSeparator(24, 1);

            using(ImRaii.Disabled(Capability.CameraEntity.CameraID == 0))
            {
                if(ImBrio.HoldButton("CameraLifetime_destroy", global::Brio.Resources.Localize.Text(""), FontAwesomeIcon.Trash, 1f, centerTest: true, tooltip: global::Brio.Resources.Localize.Text("[HOLD TO DESTROY]"), onlyIcon: true))
                {
                    Capability.VirtualCameraManager.DestroyCamera(Capability.CameraEntity.CameraID);
                }

                ImBrio.VerticalSeparator(24, 1);

                if(ImBrio.FontIconButton("CameraLifetime_rename", FontAwesomeIcon.Signature, global::Brio.Resources.Localize.Text("Rename")))
                {
                    ModalManager.Instance.OpenRenameModal(Capability.Entity);
                }
            }

            ImGui.SameLine();

            var isLocked = Capability.Entity.IsLocked;
            var lockIcon = isLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.Unlock;
            if(ImBrio.ToggelFontIconButton("CameraLifetime_lock", lockIcon, new Vector2(25, 0), isLocked,
                tooltip: global::Brio.Resources.Localize.Text(isLocked ? "Locked" : "Unlocked")))
            {
                Capability.Entity.IsLocked = !Capability.Entity.IsLocked;
            }
        }
    }

    public override void DrawPopup()
    {
        if(Capability.IsAllowed == false)
            return;

        using(ImRaii.Disabled(Capability.CameraEntity.IsDefaultCamera))
        {
            if(ImGui.MenuItem(global::Brio.Resources.Localize.Format("Rename {0}###CameraLifetime_rename", Capability.CameraEntity.FriendlyName)))
            {
                ImGui.CloseCurrentPopup();

                ModalManager.Instance.OpenRenameModal(Capability.Entity);
            }
        }

        if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Clone###CameraLifetime_clone")))
        {
            Capability.VirtualCameraManager.CloneCamera(Capability.CameraEntity.CameraID);
        }

        if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Target###CameraLifetime_target")))
        {
            Capability.VirtualCameraManager.SelectCamera(Capability.VirtualCamera);
        }

        var lockLabel = Capability.Entity.IsLocked ? "Unlock" : "Lock";
        if(ImGui.MenuItem($"{lockLabel}###CameraLifetime_lock"))
        {
            Capability.Entity.IsLocked = !Capability.Entity.IsLocked;
        }

        if(Capability.CanDestroy)
        {
            ImGui.Separator();

            if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("Destroy###CameraLifetime_destroy")))
            {
                if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Confirm Destruction###CameraLifetime_destroy_confirm")))
                {
                    Capability.VirtualCameraManager.DestroyCamera(Capability.CameraEntity.CameraID);
                }

                ImGui.EndMenu();
            }
        }
    }
}
