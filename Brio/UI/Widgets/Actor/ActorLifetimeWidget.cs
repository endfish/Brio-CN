using Brio.Capabilities.Actor;
using Brio.Game.Actor;
using Brio.Game.Camera;
using Brio.Game.World;
using Brio.Resources;
using Brio.UI.Controls;
using Brio.UI.Controls.Editors;
using Brio.UI.Controls.Stateless;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace Brio.UI.Widgets.Actor;

public class ActorLifetimeWidget : Widget<ActorLifetimeCapability>
{
    private readonly ActorSpawnService _actorSpawnService;
    private readonly VirtualCameraManager _cameraManager;
    private readonly LightingService _lightingService;

    public ActorLifetimeWidget(ActorLifetimeCapability capability, ActorSpawnService actorSpawnService, VirtualCameraManager cameraManager, LightingService lightingService) : base(capability)
    {
        _actorSpawnService = actorSpawnService;
        _cameraManager = cameraManager;
        _lightingService = lightingService;
    }

    public override string HeaderName => Localize.Get("ui.widgets.lifetime", "Lifetime");

    public override WidgetFlags Flags => WidgetFlags.DrawPopup | WidgetFlags.DrawQuickIcons;

    public override void DrawQuickIcons()
    {
        if(ImBrio.FontIconButton("lifetimewidget_spawnnew", FontAwesomeIcon.Plus, Localize.Get("ui.actor.spawnNew", "Spawn New")))
        {
            ImGui.OpenPopup("UnifiedSpawnMenuPopup");
        }
        SpawnMenuEditor.DrawUnifiedSpawnMenu(_actorSpawnService, _cameraManager, _lightingService);

        ImGui.SameLine();

        if(ImBrio.FontIconButton("lifetimewidget_spawn_prop", FontAwesomeIcon.Cubes, Localize.Get("ui.actor.spawnProp", "Spawn Prop")))
        {
            Capability.SpawnNewProp(false);
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButton("lifetimewidget_move_to_camera", FontAwesomeIcon.Thumbtack, Localize.Get("ui.actor.moveToCamera", "Move to Camera")))
        {
            Capability.MoveToCamera();
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButton("lifetimewidget_clone", FontAwesomeIcon.Clone, Localize.Get("ui.common.clone", "Clone"), Capability.CanClone))
        {
            Capability.Clone(false);
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButton("lifetimewidget_target", FontAwesomeIcon.Bullseye, Localize.Get("ui.common.target", "Target")))
        {
            Capability.Target();
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButton("lifetimewidget_destroy", FontAwesomeIcon.Trash, Localize.Get("ui.common.destroy", "Destroy"), Capability.CanDestroy))
        {
            Capability.Destroy();
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButton("lifetimewidget_rename", FontAwesomeIcon.Signature, Localize.Get("ui.common.rename", "Rename")))
        {
            RenameActorModal.Open(Capability.Actor);
        }
    }

    public override void DrawPopup()
    {
        if(ImGui.MenuItem($"{Localize.Get("ui.actor.moveToCamera", "Move to Camera")}###actorlifetime_move_to_camera"))
        {
            Capability.MoveToCamera();
        }

        if(Capability.CanClone)
        {
            if(ImGui.MenuItem($"{Localize.Get("ui.common.clone", "Clone")}###actorlifetime_clone"))
            {
                Capability.Clone(true);
            }
        }

        if(Capability.CanDestroy)
        {
            if(ImGui.BeginMenu($"{Localize.Get("ui.common.destroy", "Destroy")}###actorlifetime_destroy"))
            {
                if(ImGui.MenuItem($"{Localize.Get("ui.common.confirmDestruction", "Confirm Destruction")}###actorlifetime_destroy_confirm"))
                {
                    Capability.Destroy();
                }

                ImGui.EndMenu();
            }
        }

        if(ImGui.MenuItem($"{Localize.Get("ui.common.rename", "Rename")} {Capability.Actor.FriendlyName}###actorlifetime_rename"))
        {
            ImGui.CloseCurrentPopup();

            RenameActorModal.Open(Capability.Actor);
        }

        if(ImGui.MenuItem($"{Localize.Get("ui.common.target", "Target")}###actorlifetime_target"))
        {
            Capability.Target();
        }

    }
}
