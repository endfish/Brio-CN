using Brio.Capabilities.Actor;
using Brio.UI.Controls.Stateless;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace Brio.UI.Widgets.Actor;

public class ActorLifetimeWidget(ActorLifetimeCapability capability) : Widget<ActorLifetimeCapability>(capability)
{
    public override string HeaderName => "Lifetime";

    public override WidgetFlags Flags => WidgetFlags.DrawPopup | WidgetFlags.DrawQuickIcons;

    public override void DrawQuickIcons()
    {
        if(ImBrio.FontIconButton("lifetimewidget_clone", FontAwesomeIcon.Clone, global::Brio.Resources.Localize.Text("Clone"), Capability.CanClone))
        {
            Capability.Clone(false);
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButton("lifetimewidget_target", FontAwesomeIcon.Bullseye, global::Brio.Resources.Localize.Text("Target")))
        {
            Capability.Target();
        }

        ImBrio.VerticalSeparator(24, 1);

        if(ImBrio.HoldButton("lifetimewidget_destroy", global::Brio.Resources.Localize.Text(""), FontAwesomeIcon.Trash, 1f, new(40, 0), centerTest: true, tooltip: global::Brio.Resources.Localize.Text("[HOLD TO DESTROY]"), onlyIcon: true))
        {
            Capability.Destroy();
        }

        ImBrio.VerticalSeparator(24, 1);

        if(ImBrio.FontIconButton("lifetimewidget_rename", FontAwesomeIcon.Signature, global::Brio.Resources.Localize.Text("Rename")))
        {
            ModalManager.Instance.OpenRenameModal(Capability.Actor);
        }
    }

    public override void DrawPopup()
    {
        if(ImGui.MenuItem($"Rename {Capability.Actor.FriendlyName}###actorlifetime_rename"))
        {
            ImGui.CloseCurrentPopup();

            ModalManager.Instance.OpenRenameModal(Capability.Actor);
        }

        if(Capability.CanClone)
        {
            if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Clone###actorlifetime_clone")))
            {
                Capability.Clone(true);
            }
        }

        if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Move to Camera###actorlifetime_move_to_camera")))
        {
            Capability.MoveToCamera();
        }

        if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Target###actorlifetime_target")))
        {
            Capability.Target();
        }

        if(Capability.Entity.TryGetCapability<ActorAppearanceCapability>(out var appearance))
        {
            var toggele = appearance.IsHidden ? "Show" : "Hide";
            if(ImGui.MenuItem($"{toggele} {Capability.Actor.FriendlyName}###Appearance_popup_toggle"))
                appearance.ToggleHide();
        }

        if(Capability.CanDestroy)
        {
            ImGui.Separator();

            if(ImGui.BeginMenu("Destroy###actorlifetime_destroy"))
            {
                if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Confirm Destruction###actorlifetime_destroy_confirm")))
                {
                    Capability.Destroy();
                }

                ImGui.EndMenu();
            }
        }
    }
}
