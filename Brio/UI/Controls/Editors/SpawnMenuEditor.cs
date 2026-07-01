using Brio.Entities.Camera;
using Brio.Game.Actor;
using Brio.Game.Camera;
using Brio.Game.World;
using Brio.UI.Controls.Core;
using Brio.UI.Controls.Stateless;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Brio.UI.Controls.Editors;

public static class SpawnMenuEditor
{
    private const float MenuWidth = 220f;

    public static void DrawUnifiedSpawnMenu(
        ActorSpawnService? actorSpawnService = null,
        VirtualCameraManager? cameraManager = null,
        LightingService? lightingService = null)
    {
        using var popup = ImRaii.Popup("UnifiedSpawnMenuPopup");
        if(!popup.Success)
            return;

        var buttonSize = new Vector2(MenuWidth * ImGuiHelpers.GlobalScale, 0);

        using(ImRaii.PushColor(ImGuiCol.Button, UIConstants.Transparent))
        {
            // Actor spawn
            if(actorSpawnService != null)
            {
                ImGui.Text(global::Brio.Resources.Localize.Get("ui.entities.actors", "Actors"));
                ImGui.Separator();

                if(ImBrio.DrawIconButton(FontAwesomeIcon.User, global::Brio.Resources.Localize.Get("ui.actor.spawnNewActor", "Spawn New Actor"), buttonSize))
                {
                    actorSpawnService.CreateCharacter(out _, SpawnFlags.Default, true);
                    ImGui.CloseCurrentPopup();
                }

                if(ImBrio.DrawIconButton(FontAwesomeIcon.PlusSquare, global::Brio.Resources.Localize.Get("ui.actor.spawnWithCompanionSlot", "Spawn with Companion Slot"), buttonSize))
                {
                    actorSpawnService.CreateCharacter(out _, SpawnFlags.WithCompanionSlot, false);
                    ImGui.CloseCurrentPopup();
                }

                if(ImBrio.DrawIconButton(FontAwesomeIcon.Cubes, global::Brio.Resources.Localize.Get("ui.actor.spawnProp", "Spawn Prop"), buttonSize))
                {
                    actorSpawnService.SpawnNewProp(out _);
                    ImGui.CloseCurrentPopup();
                }
            }

            // Camera spawn
            if(cameraManager != null)
            {
                if(actorSpawnService != null)
                    ImGui.Spacing();

                ImGui.Text(global::Brio.Resources.Localize.Get("ui.entities.cameras", "Cameras"));
                ImGui.Separator();

                if(ImBrio.DrawIconButton(FontAwesomeIcon.Camera, global::Brio.Resources.Localize.Get("ui.camera.newBrioCamera", "New Brio Camera"), buttonSize))
                {
                    cameraManager.CreateCamera(CameraType.Game);
                    ImGui.CloseCurrentPopup();
                }

                if(ImBrio.DrawIconButton(FontAwesomeIcon.Video, global::Brio.Resources.Localize.Get("ui.camera.newFreeCam", "New Free-Cam"), buttonSize))
                {
                    cameraManager.CreateCamera(CameraType.Free);
                    ImGui.CloseCurrentPopup();
                }
            }

            // Light spawn
            if(lightingService != null)
            {
                if(actorSpawnService != null || cameraManager != null)
                    ImGui.Spacing();

                ImGui.Text(global::Brio.Resources.Localize.Get("ui.environment.lights", "Lights"));
                ImGui.Separator();

                if(ImBrio.DrawIconButton(FontAwesomeIcon.Lightbulb, global::Brio.Resources.Localize.Get("ui.environment.spotLight", "Spawn Spot Light"), buttonSize))
                {
                    lightingService.SpawnLight(LightType.SpotLight);
                    ImGui.CloseCurrentPopup();
                }

                if(ImBrio.DrawIconButton(FontAwesomeIcon.Circle, global::Brio.Resources.Localize.Get("ui.environment.pointLight", "Spawn Point Light"), buttonSize))
                {
                    lightingService.SpawnLight(LightType.PointLight);
                    ImGui.CloseCurrentPopup();
                }

                if(ImBrio.DrawIconButton(FontAwesomeIcon.Square, global::Brio.Resources.Localize.Get("ui.environment.flatLight", "Spawn Flat Light"), buttonSize))
                {
                    lightingService.SpawnLight(LightType.FlatLight);
                    ImGui.CloseCurrentPopup();
                }
            }
        }
    }
}

