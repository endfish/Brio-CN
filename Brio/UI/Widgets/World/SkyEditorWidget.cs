using Brio.Capabilities.World;
using Brio.Game.World;
using Brio.UI.Controls.Selectors;
using Brio.UI.Controls.Stateless;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Brio.UI.Widgets.World;

public class SkyEditorWidget(SkyEditorCapability skyEditorCapability) : Widget<SkyEditorCapability>(skyEditorCapability)
{
    public override string HeaderName => "Ambient";
    public override WidgetFlags Flags => WidgetFlags.DrawBody;

    int selected = 0;
    private readonly TextureSelector _skyTextureSelector = new("sky_texture_selector", TextureType.Sky, 450);
    private readonly TextureSelector _cloudTextureSelector = new("cloud_texture_selector", TextureType.Cloud, 75);
    private readonly TextureSelector _cloudSideTextureSelector = new("cloud_side_texture_selector", TextureType.CloudSide, 75);

    public unsafe override void DrawBody()
    {
        var env = BrioEnvManager.Instance();
        if(env == null) return;

        ImBrio.VerticalPadding(3);

        ImBrio.ButtonSelectorStrip("stars_filters_selector", new Vector2(ImBrio.GetRemainingWidth(), ImBrio.GetLineHeight()), ref selected, ["Sky", "Stars", "Clouds", "Indoors"]);

        switch(selected)
        {
            case 1:
                ImBrio.VerticalPadding(3);

                if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Stars"), FontAwesomeIcon.Redo, global::Brio.Resources.Localize.Text("Reset All Stars Properties"),
                    Capability.Environment.EnvironmentOverrideState.HasFlag(EnvironmentOverrideState.Stars)))
                {
                    Capability.Environment.EnvironmentOverrideState &= ~EnvironmentOverrideState.Stars;
                }

                ImBrio.CenterNextElementWithPadding(15);
                var didSkyChange2 = ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###starcount")u8, ref env->EnvState.Stars.StarCount, 0.0f, 20.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Star Count"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange2 |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###starcountIntensity")u8, ref env->EnvState.Stars.StarIntensity, 0.0f, 2.5f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Star Intensity"));

                ImBrio.VerticalPadding(5);
                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Moon Color and Moon Brightness"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange2 |= ImGui.ColorEdit4(global::Brio.Resources.Localize.Text("###moonColor")u8, ref env->EnvState.Stars.MoonColor);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Moon Color"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange2 |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###MoonBrightness")u8, ref env->EnvState.Stars.MoonBrightness, 0.0f, 1.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Moon Brightness"));

                ImBrio.VerticalPadding(5);
                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Constellation Properties"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange2 |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###constellationCount")u8, ref env->EnvState.Stars.ConstellationCount, 0.0f, 10.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Constellation Count"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange2 |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###constellationsIntensity")u8, ref env->EnvState.Stars.ConstellationIntensity, 0.0f, 2.5f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Constellations Intensity"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange2 |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###galaxyIntensity")u8, ref env->EnvState.Stars.GalaxyIntensity, 0.0f, 10.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Galaxy Intensity"));

                ImBrio.VerticalPadding(3);

                if(didSkyChange2)
                    Capability.Environment.EnvironmentOverrideState |= EnvironmentOverrideState.Stars;

                break;
            case 0:
                ImBrio.VerticalPadding(3);

                if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Sky"), FontAwesomeIcon.Redo, global::Brio.Resources.Localize.Text("Reset All Sky Properties"),
                    Capability.Environment.EnvironmentOverrideState.HasFlag(EnvironmentOverrideState.Sky)))
                {
                    Capability.Environment.EnvironmentOverrideState &= ~EnvironmentOverrideState.Sky;
                }

                if(ImBrio.BorderedGameTex("##skyTexturePreview", _skyTextureSelector.GetTexturePath(env->EnvState.SkyTextureID)))
                {
                    _skyTextureSelector.Select(new TextureId(env->EnvState.SkyTextureID));
                    ImGui.OpenPopup("sky_texture_selector"u8);
                }
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Click to open texture selector"));

                var didSkyChange = false;

                using(var popup = ImRaii.Popup("sky_texture_selector"u8))
                {
                    if(popup.Success)
                    {
                        _skyTextureSelector.Draw();

                        if(_skyTextureSelector.SoftSelectionChanged && _skyTextureSelector.SoftSelected != null)
                        {
                            env->EnvState.SkyTextureID = _skyTextureSelector.SoftSelected.Id;
                            Capability.Environment.EnvironmentOverrideState |= EnvironmentOverrideState.Sky;
                            didSkyChange = true;
                        }

                        if(_skyTextureSelector.SelectionChanged)
                            ImGui.CloseCurrentPopup();
                    }
                }

                ImGui.SameLine();
                ImBrio.CenterNextElementWithPadding(10);
                ImBrio.VerticalPadding(5);
                didSkyChange |= ImGui.InputUInt("###SkyTextureID"u8, ref env->EnvState.SkyTextureID);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Sky Texture ID"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###fogSunVisibility")u8, ref env->EnvState.Fog.SunVisibility, 0.0f, 1f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Sun Visibility"));

                if(didSkyChange)
                    Capability.Environment.EnvironmentOverrideState |= EnvironmentOverrideState.Sky;

                ImBrio.VerticalPadding(5);

                if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Ambient Lighting"), FontAwesomeIcon.Redo, global::Brio.Resources.Localize.Text("Reset All Lighting Properties"),
                    Capability.Environment.EnvironmentOverrideState.HasFlag(EnvironmentOverrideState.EnvironmentLighting)))
                {
                    Capability.Environment.EnvironmentOverrideState &= ~EnvironmentOverrideState.EnvironmentLighting;
                }

                ImBrio.VerticalPadding(2);
                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Temperature & Saturation"));

                ImBrio.CenterNextElementWithPadding(15);
                var didSkyChange3 = ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###temperatureColor")u8, ref env->EnvState.EnvironmentLighting.AmbientTemperature, -2.5f, 2.5f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Ambient Temperature Color"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange3 |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###saturationColor")u8, ref env->EnvState.EnvironmentLighting.AmbientSaturation, 0.0f, 5.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Ambient Saturation Color"));

                ImBrio.VerticalPadding(5);
                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Ambient Color"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange3 |= ImGui.ColorEdit3(global::Brio.Resources.Localize.Text("##ambientColor")u8, ref env->EnvState.EnvironmentLighting.AmbientColor);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Ambient Color"));

                ImBrio.VerticalPadding(5);
                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Sunlight & Moonlight Color"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange3 |= ImGui.ColorEdit3(global::Brio.Resources.Localize.Text("###sunlightColor")u8, ref env->EnvState.EnvironmentLighting.SunlightColor);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Sunlight Color"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange3 |= ImGui.ColorEdit3(global::Brio.Resources.Localize.Text("###moonlightColor")u8, ref env->EnvState.EnvironmentLighting.MoonlightColor);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Moonlight Color"));

                ImBrio.VerticalPadding(3);

                if(didSkyChange3)
                    Capability.Environment.EnvironmentOverrideState |= EnvironmentOverrideState.EnvironmentLighting;

                break;
            case 2:

                ImBrio.VerticalPadding(3);

                if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Cloud"), FontAwesomeIcon.Redo, global::Brio.Resources.Localize.Text("Reset All Cloud Properties"),
                    Capability.Environment.EnvironmentOverrideState.HasFlag(EnvironmentOverrideState.Clouds)))
                {
                    Capability.Environment.EnvironmentOverrideState &= ~EnvironmentOverrideState.Clouds;
                }

                if(ImBrio.BorderedGameTex("##cloudTexturePreview", _cloudTextureSelector.GetTexturePath(env->EnvState.Clouds.CloudTexture)))
                {
                    _cloudTextureSelector.Select(new TextureId(env->EnvState.Clouds.CloudTexture));
                    ImGui.OpenPopup("cloud_texture_selector"u8);
                }
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Click to change Cloud Texture"));

                var didSkyChange4 = false;

                using(var popup = ImRaii.Popup("cloud_texture_selector"u8))
                {
                    if(popup.Success)
                    {
                        _cloudTextureSelector.Draw();

                        if(_cloudTextureSelector.SoftSelectionChanged && _cloudTextureSelector.SoftSelected != null)
                        {
                            env->EnvState.Clouds.CloudTexture = _cloudTextureSelector.SoftSelected.Id;
                            Capability.Environment.EnvironmentOverrideState |= EnvironmentOverrideState.Clouds;
                            didSkyChange4 = true;
                        }

                        if(_cloudTextureSelector.SelectionChanged)
                            ImGui.CloseCurrentPopup();
                    }
                }

                ImGui.SameLine();
                ImBrio.CenterNextElementWithPadding(10);
                ImBrio.VerticalPadding(5);
                didSkyChange4 |= ImGui.InputUInt("###CloudTexture"u8, ref env->EnvState.Clouds.CloudTexture);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Cloud Texture ID"));

                if(ImBrio.BorderedGameTex("##cloudSideTexturePreview", _cloudSideTextureSelector.GetTexturePath(env->EnvState.Clouds.CloudSideTexture)))
                {
                    _cloudSideTextureSelector.Select(new TextureId(env->EnvState.Clouds.CloudSideTexture));
                    ImGui.OpenPopup("cloud_side_texture_selector"u8);
                }
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Click to change Cloud Side Texture"));

                using(var popup = ImRaii.Popup("cloud_side_texture_selector"u8))
                {
                    if(popup.Success)
                    {
                        _cloudSideTextureSelector.Draw();

                        if(_cloudSideTextureSelector.SoftSelectionChanged && _cloudSideTextureSelector.SoftSelected != null)
                        {
                            env->EnvState.Clouds.CloudSideTexture = _cloudSideTextureSelector.SoftSelected.Id;
                            Capability.Environment.EnvironmentOverrideState |= EnvironmentOverrideState.Clouds;
                            didSkyChange4 = true;
                        }

                        if(_cloudSideTextureSelector.SelectionChanged)
                            ImGui.CloseCurrentPopup();
                    }
                }

                ImGui.SameLine();
                ImBrio.CenterNextElementWithPadding(10);
                ImBrio.VerticalPadding(5);
                didSkyChange4 |= ImGui.InputUInt("###CloudSideTexture"u8, ref env->EnvState.Clouds.CloudSideTexture);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Cloud Side Texture ID"));

                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Cloud Color"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange4 |= ImGui.ColorEdit3(global::Brio.Resources.Localize.Text("###leftCloudColor"), ref env->EnvState.Clouds.CloudColor1);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Cloud Color"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange4 |= ImGui.ColorEdit3(global::Brio.Resources.Localize.Text("###rightcloudColor"), ref env->EnvState.Clouds.CloudColor2);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Cloud Side Color"));

                ImBrio.VerticalPadding(5);
                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Cloud Other"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange4 |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###gradientStop"), ref env->EnvState.Clouds.ShadowStop, 0.0f, 2.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Shadow Stop"));

                ImBrio.CenterNextElementWithPadding(15);
                didSkyChange4 |= ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###cloudHeight"), ref env->EnvState.Clouds.CloudHeight, 0.0f, 2.0f);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Cloud Height"));

                ImBrio.VerticalPadding(3);

                if(didSkyChange4)
                    Capability.Environment.EnvironmentOverrideState |= EnvironmentOverrideState.Clouds;

                break;
            case 3:

                ImBrio.VerticalPadding(3);

                if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Interior Brightness"), FontAwesomeIcon.Redo, global::Brio.Resources.Localize.Text("Reset All Sky Properties"), Capability.IsInside))
                {
                    Capability.ResetIndoorLighting();
                }

                ImBrio.CenterNextElementWithPadding(10);

                float currentLight = Capability.IndoorLight;
                using(ImRaii.Disabled(Capability.IsInside == false))
                    if(ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###brightness"), ref currentLight, 0.0f, 1.0f))
                    {
                        Capability.IndoorLight = currentLight;
                    }
                if(Capability.IsInside == false)
                    ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("You must be inside housing to adjust interior brightness."));
                else
                    ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Adjust the brightness of the interior lighting."));

                ImBrio.VerticalPadding(3);

                break;
        }
    }
}
