using Brio.Capabilities.World;
using Brio.Game.World.Interop;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Linq;

namespace Brio.UI.Widgets.World.Lights;

public class LightContainerWidget(LightContainerCapability capability) : Widget<LightContainerCapability>(capability)
{
    public override string HeaderName => "Lights";

    public override WidgetFlags Flags => WidgetFlags.DrawPopup;

    public override void DrawPopup()
    {
        using(ImRaii.Disabled(Capability.IsAllowed == false))
        {
            if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("Add from World...###containerwidgetpopup_add")))
            {
                if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("World Light...###containerwidgetpopup_addWorldLight")))
                {
                    var worldLights = Capability.GetWorldLights().OrderBy(x => x.distance).ToList();
                    if(worldLights.Count == 0)
                    {
                        ImGui.TextDisabled(global::Brio.Resources.Localize.Text("No world lights found"));
                    }
                    else
                    {
                        if(ImGui.MenuItem(global::Brio.Resources.Localize.Format("Add All ({0})###containerwidgetpopup_addAllWorldLights", worldLights.Count)))
                        {
                            Capability.AddAllWorldLights();
                        }
                        ImGui.Separator();
                        foreach(var (light, distance) in worldLights)
                        {
                            if(ImGui.MenuItem(global::Brio.Resources.Localize.Format("Light: {0:F1}y##worldlight_{1}", distance, light)))
                            {
                                Capability.AddWorldLight(light);
                            }
                        }
                    }
                    ImGui.EndMenu();
                }
                ImGui.EndMenu();
            }

            if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Open Light Window###containerwidgetpopup_openWindow")))
            {
                Capability.OpenLightWindow();
            }

            if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("New...###containerwidgetpopup_new")))
            {
                ImGui.Separator();

                if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Spot Light###containerwidgetpopup_spawn_SpotLight")))
                {
                    Capability.LightingService.SpawnLight(LightType.SpotLight);
                }
                if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Area Light###containerwidgetpopup_spawn_SpotLight")))
                {
                    Capability.LightingService.SpawnLight(LightType.PointLight);
                }
                if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Flat Light###containerwidgetpopup_spawn_SpotLight")))
                {
                    Capability.LightingService.SpawnLight(LightType.FlatLight);
                }
                ImGui.EndMenu();
            }

            if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("Destroy All...###containerwidgetpopup_destroy")))
            {
                if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("Lights###containerwidgetpopup_destroyLights")))
                {
                    if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Confirm Destruction##containerwidgetpopup_destroyallLights")))
                    {
                        Capability.LightingService.DestroyAll();
                    }
                    ImGui.EndMenu();
                }
                ImGui.EndMenu();
            }
        }
    }
}
