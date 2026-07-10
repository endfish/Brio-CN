using Brio.Capabilities.World;
using Brio.Game.World;
using Brio.UI.Controls.Stateless;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;

namespace Brio.UI.Widgets.World;

public class DebugEnvironmentWidget(DebugEnvironmentCapability capability) : Widget<DebugEnvironmentCapability>(capability)
{
    public override string HeaderName => "Debug";

    public override WidgetFlags Flags => WidgetFlags.DrawBody;

    override unsafe public void DrawBody()
    {
        if(ImBrio.ToggelButton("isParticleSystemEnabled", Capability.Environment.isParticleSystemEnabled))
            Capability.Environment.isParticleSystemEnabled = !Capability.Environment.isParticleSystemEnabled;

        if(ImBrio.ToggelButton("IsEnvironmentOverride", Capability.Environment.IsEnvironmentOverride))
            Capability.Environment.IsEnvironmentOverride = !Capability.Environment.IsEnvironmentOverride;

        if(ImBrio.ToggelButton("customParticlesEnabled", Capability.Environment.customParticlesEnabled))
            Capability.Environment.customParticlesEnabled = !Capability.Environment.customParticlesEnabled;

        var env = BrioEnvManager.Instance();
        if(env == null) return;

        ImBrio.VerticalPadding(5);

        ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###11")u8, ref env->EnvState.EnvironmentLighting.Unknown1, 0.0f, 100f);
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("EnvironmentLighting.Unknown1"));
        ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###21")u8, ref env->EnvState.EnvironmentLighting.Unknown2, 0.0f, 100f);
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("EnvironmentLighting.Unknown2"));
        ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###31")u8, ref env->EnvState.EnvironmentLighting.LightDistance, 0.0f, 100f);
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("EnvironmentLighting.LightDistance"));
        ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###41")u8, ref env->EnvState.EnvironmentLighting.Unknown4, 0.0f, 100f);
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("EnvironmentLighting.Unknown4"));

        ImBrio.VerticalPadding(3);

        ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###13")u8, ref env->EnvState.Rain.Unknown1, 0.0f, 100f);
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Rain.Unknown1"));
        ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###23")u8, ref env->EnvState.Rain.Unknown2, 0.0f, 100f);
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Rain.Unknown2"));
        ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###33")u8, ref env->EnvState.Rain.Unknown3, 0.0f, 100f);
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Rain.LightDistance"));

        ImBrio.VerticalPadding(3);

        ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###14")u8, ref env->EnvState.Particles.Unknown1, 0.0f, 100f);
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Particles.Unknown1"));

        ImBrio.VerticalPadding(5);

        ImGui.Text(global::Brio.Resources.Localize.Text("EnvManager:")u8);
        Capability.DynamisIPC.DrawPointer(&env);

        ImGui.Text(global::Brio.Resources.Localize.Text("EnvState:")u8);
        Capability.DynamisIPC.DrawPointer(&env->EnvState);

        ImBrio.VerticalPadding(5);

        ImGui.Text(global::Brio.Resources.Localize.Text("EnvironmentLighting:")u8);
        Capability.DynamisIPC.DrawPointer(&env->EnvState.EnvironmentLighting);
        ImGui.Text(global::Brio.Resources.Localize.Text("Stars:")u8);
        Capability.DynamisIPC.DrawPointer(&env->EnvState.Stars);
        ImGui.Text(global::Brio.Resources.Localize.Text("Fog:")u8);
        Capability.DynamisIPC.DrawPointer(&env->EnvState.Fog);
        ImGui.Text(global::Brio.Resources.Localize.Text("Clouds:")u8);
        Capability.DynamisIPC.DrawPointer(&env->EnvState.Clouds);
        ImGui.Text(global::Brio.Resources.Localize.Text("Rain:")u8);
        Capability.DynamisIPC.DrawPointer(&env->EnvState.Rain);
        ImGui.Text(global::Brio.Resources.Localize.Text("Particles:")u8);
        Capability.DynamisIPC.DrawPointer(&env->EnvState.Particles);
        ImGui.Text(global::Brio.Resources.Localize.Text("Wind:")u8);
        Capability.DynamisIPC.DrawPointer(&env->EnvState.Wind);
    }
}
