using Brio.Capabilities.World;
using Brio.Game.World;
using Brio.UI.Controls.Selectors;
using Brio.UI.Controls.Stateless;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Numerics;

namespace Brio.UI.Widgets.World;

public class EnvironmentEditorWidget(EnvironmentEditorCapability capability) : Widget<EnvironmentEditorCapability>(capability)
{
    public override string HeaderName => "Environment";
    public override WidgetFlags Flags => WidgetFlags.DrawBody;

    int selected = 0;
    private readonly TextureSelector _textureSelector = new("particle_texture_selector", TextureType.Particle, 20);

    public unsafe override void DrawBody()
    {
        ImBrio.ButtonSelectorStrip("environment_filters_selector", new Vector2(ImBrio.GetRemainingWidth(), ImBrio.GetLineHeight()), ref selected, ["Particles", "Rain", "Wind", "Fog"]);

        var env = BrioEnvManager.Instance();
        if(env == null) return;

        switch(selected)
        {
            case 0:
                ImBrio.VerticalPadding(3);

                if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Particles"), FontAwesomeIcon.Redo, global::Brio.Resources.Localize.Text("Reset All Particle Properties"),
                     Capability.Environment.EnvironmentOverrideState.HasFlag(EnvironmentOverrideState.Particles)))
                {
                    Capability.Environment.EnvironmentOverrideState &= ~EnvironmentOverrideState.Particles;
                }

                var path = $"bgcommon/nature/dust/texture/dust_{Math.Max(0, env->EnvState.Particles.TextureId - 2):D3}.tex";
                if(ImBrio.BorderedGameTex("##particleTexturePreview", path))
                {
                    _textureSelector.Select(new TextureId(env->EnvState.Particles.TextureId));
                    ImGui.OpenPopup("particle_texture_selector"u8);
                }
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Click to open texture selector"));

                bool didParticlesChange = false;

                using(var popup = ImRaii.Popup("particle_texture_selector"u8))
                {
                    if(popup.Success)
                    {
                        _textureSelector.Draw();

                        if(_textureSelector.SoftSelectionChanged && _textureSelector.SoftSelected != null)
                        {
                            env->EnvState.Particles.TextureId = _textureSelector.SoftSelected.Id;
                            Capability.Environment.EnvironmentOverrideState |= EnvironmentOverrideState.Particles;
                            didParticlesChange = true;
                        }

                        if(_textureSelector.SelectionChanged)
                            ImGui.CloseCurrentPopup();
                    }
                }

                ImGui.SameLine();
                ImBrio.CenterNextElementWithPadding(10);
                ImBrio.VerticalPadding(5);
                didParticlesChange |= ImGui.InputUInt("###particleTexture"u8, ref env->EnvState.Particles.TextureId);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Particle Texture ID"));

                ImBrio.VerticalPadding(5);
                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Particle Properties"));

                ImBrio.CenterNextElementWithPadding(15);
                didParticlesChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###particleIntensity"), ref env->EnvState.Particles.Intensity, 0.0f, 1.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Particle Count"));

                ImBrio.CenterNextElementWithPadding(15);
                didParticlesChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###particleSize"), ref env->EnvState.Particles.Size, 0.0f, 20.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Particle Size"));

                ImBrio.CenterNextElementWithPadding(15);
                didParticlesChange |= ImGui.ColorEdit4(global::Brio.Resources.Localize.Text("###particleColor"), ref env->EnvState.Particles.Color);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Particle Color"));

                ImBrio.CenterNextElementWithPadding(15);
                didParticlesChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###particleGlow"), ref env->EnvState.Particles.Glow, 0.0f, 10.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Particle Glow"));

                ImBrio.VerticalPadding(5);
                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Particle Sub-Properties"));

                ImBrio.CenterNextElementWithPadding(15);
                didParticlesChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###particleSpread"), ref env->EnvState.Particles.Spread, 0.0f, 10.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Particle Spread"));

                ImBrio.CenterNextElementWithPadding(15);
                didParticlesChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###particleWeight"), ref env->EnvState.Particles.Weight, 0.0f, 10.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Particle Weight"));

                ImBrio.CenterNextElementWithPadding(15);
                didParticlesChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###particleSpeed"), ref env->EnvState.Particles.Speed, 0.0f, 1.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Particle Speed"));

                ImBrio.CenterNextElementWithPadding(15);
                didParticlesChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###particleSpin"), ref env->EnvState.Particles.Spin, 0.05f, 5.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Particle Spin"));

                ImBrio.VerticalPadding(3);

                if(didParticlesChange)
                    Capability.Environment.EnvironmentOverrideState |= EnvironmentOverrideState.Particles;

                break;
            case 1:
                ImBrio.VerticalPadding(3);

                if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Rain"), FontAwesomeIcon.Redo, global::Brio.Resources.Localize.Text("Reset All Rain Properties"),
                    Capability.Environment.EnvironmentOverrideState.HasFlag(EnvironmentOverrideState.Rain)))
                {
                    Capability.Environment.EnvironmentOverrideState &= ~EnvironmentOverrideState.Rain;
                }

                ImBrio.CenterNextElementWithPadding(15);
                var didRainChange = ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###rainIntensity"), ref env->EnvState.Rain.Intensity, 0.0f, 1.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Rain Intensity"));

                ImBrio.CenterNextElementWithPadding(15);
                didRainChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###rainThickness"), ref env->EnvState.Rain.Size, 0.0f, 1.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Rain Line Thickness"));

                ImBrio.CenterNextElementWithPadding(15);
                didRainChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###rainWeight"), ref env->EnvState.Rain.Weight, 0.0f, 10.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Rain Weight"));

                ImBrio.VerticalPadding(5);
                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Color"));

                ImBrio.CenterNextElementWithPadding(15);
                didRainChange |= ImGui.ColorEdit4(global::Brio.Resources.Localize.Text("###rainColor"), ref env->EnvState.Rain.Color);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Rain Color"));

                ImBrio.VerticalPadding(5);
                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Advanced"));

                ImBrio.CenterNextElementWithPadding(15);
                didRainChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###rainScattering"), ref env->EnvState.Rain.Scatter, 0.0f, 10.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Rain Scattering"));

                ImBrio.CenterNextElementWithPadding(15);
                didRainChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###rainRaindrops"), ref env->EnvState.Rain.Raindrops, 0.0f, 1.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Raindrops"));

                ImBrio.VerticalPadding(3);

                if(didRainChange)
                    Capability.Environment.EnvironmentOverrideState |= EnvironmentOverrideState.Rain;

                break;
            case 2:
                ImBrio.VerticalPadding(3);

                if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Wind"), FontAwesomeIcon.Redo, global::Brio.Resources.Localize.Text("Reset All Rain Properties"),
                    Capability.Environment.EnvironmentOverrideState.HasFlag(EnvironmentOverrideState.Wind)))
                {
                    Capability.Environment.EnvironmentOverrideState &= ~EnvironmentOverrideState.Wind;
                }

                ImBrio.CenterNextElementWithPadding(15);
                var didWindChange = ImBrio.SliderAngle("###windDirectionu", ref env->EnvState.Wind.Direction, 0.0f, MathF.PI);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Wind Direction"));

                ImBrio.CenterNextElementWithPadding(15);
                didWindChange |= ImBrio.SliderAngle("###windAngle", ref env->EnvState.Wind.Angle, 0.0f, 180.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Wind Angle"));

                ImBrio.CenterNextElementWithPadding(15);
                didWindChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###windSpeed"), ref env->EnvState.Wind.Speed, -30.0f, 100f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Wind Speed"));

                ImBrio.VerticalPadding(3);

                if(didWindChange)
                    Capability.Environment.EnvironmentOverrideState |= EnvironmentOverrideState.Wind;

                break;
            case 3:
                ImBrio.VerticalPadding(3);

                if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Fog"), FontAwesomeIcon.Redo, global::Brio.Resources.Localize.Text("Reset All Fog Properties"),
                     Capability.Environment.EnvironmentOverrideState.HasFlag(EnvironmentOverrideState.Fog)))
                {
                    Capability.Environment.EnvironmentOverrideState &= ~EnvironmentOverrideState.Fog;
                }

                ImBrio.CenterNextElementWithPadding(15);
                var didFogChange = ImGui.ColorEdit4(global::Brio.Resources.Localize.Text("###fogColor"), ref env->EnvState.Fog.Color);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Fog Color"));

                ImBrio.CenterNextElementWithPadding(15);
                didFogChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###fogDistance"), ref env->EnvState.Fog.Distance, 0.0f, 1000f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Fog Distance"));

                ImBrio.CenterNextElementWithPadding(15);
                didFogChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###fogThickness"), ref env->EnvState.Fog.Thickness, 0.0f, 50f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Fog Thickness"));

                ImBrio.CenterNextElementWithPadding(15);
                didFogChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###fogOpacity"), ref env->EnvState.Fog.FogOpacity, 0.0f, 10f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Fog Opacity"));

                ImBrio.VerticalPadding(5);
                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Sky Opacity & Smoothness"));

                ImBrio.CenterNextElementWithPadding(15);
                didFogChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###skyOpacity"), ref env->EnvState.Fog.SkyOpacity, 0.0f, 10f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Sky Opacity"));

                ImBrio.CenterNextElementWithPadding(15);
                didFogChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###skySmoothness"), ref env->EnvState.Fog.SkySmoothness, 0.0f, 1000f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Sky Smoothness"));

                ImBrio.VerticalPadding(3);

                if(didFogChange)
                    Capability.Environment.EnvironmentOverrideState |= EnvironmentOverrideState.Fog;

                break;
        }
    }
}
