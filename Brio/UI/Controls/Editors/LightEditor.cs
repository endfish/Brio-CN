using Brio.Capabilities.World;
using Brio.Game.World.Interop;
using Brio.Input;
using Brio.Services;
using Brio.UI.Controls.Stateless;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Numerics;

namespace Brio.UI.Controls.Editors;

public class LightEditor
{
    public static unsafe void DrawAdvancedShadows(LightRenderingCapability Capability)
    {
        var light = Capability.GameLight.GameLight != null ? Capability.GameLight.GameLight->RenderLight : null;
        if(light == null) return;

        // Falloff Mode
        ImGui.Text(global::Brio.Resources.Localize.Text("Light Falloff Mode:"));
        ImBrio.CenterNextElementWithPadding(15);
        if(ImGui.BeginCombo(
            global::Brio.Resources.Localize.Text("###falloffMode"),
            global::Brio.Resources.Localize.Text(light->FalloffType.ToString())))
        {
            foreach(var value in Enum.GetValues<FalloffType>())
            {
                var localizedValue = global::Brio.Resources.Localize.Text(value.ToString());
                var valueLabel = $"{localizedValue}##falloff_{value}";
                if(ImGui.Selectable(valueLabel, light->FalloffType == value))
                {
                    light->FalloffType = value;
                }
            }
            ImGui.EndCombo();
        }
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Light Falloff Mode"));

        // Shadows
        //

        ImGui.Text(global::Brio.Resources.Localize.Text("Character Shadow Range:"));
        ImBrio.CenterNextElementWithPadding(15);
        ImGui.DragFloat(global::Brio.Resources.Localize.Text("###shadowRange"), ref light->CharacterShadowRange, 0.1f, 0.001f, 1000.0f);

        ImGui.Text(global::Brio.Resources.Localize.Text("Shadow Plane Near:"));
        ImBrio.CenterNextElementWithPadding(15);
        ImGui.DragFloat(global::Brio.Resources.Localize.Text("###shadowNear"), ref light->ShadowPlaneNear, 0.01f, 0.001f, 1000.0f);

        ImGui.Text(global::Brio.Resources.Localize.Text("Shadow Plane Far:"));
        ImBrio.CenterNextElementWithPadding(15);
        ImGui.DragFloat(global::Brio.Resources.Localize.Text("###shadowFar"), ref light->ShadowPlaneFar, 0.01f, 0.001f, 1000.0f);
    }

    public static unsafe void DrawAdvancedSettings(LightRenderingCapability Capability)
    {
        var light = Capability.GameLight.GameLight != null ? Capability.GameLight.GameLight->RenderLight : null;
        if(light == null) return;

        ImBrio.VerticalPadding(5);
        ImGui.Text(global::Brio.Resources.Localize.Text("Falloff Mode / Power & Light Range"));


        ImBrio.VerticalPadding(5);
    }

    public static unsafe void DrawLightProperties(LightRenderingCapability Capability)
    {

        //
        // Hedder Buttons


        //ImGui.SameLine();

        //if(ImBrio.FontIconButtonRight("reset", FontAwesomeIcon.Undo, 1, global::Brio.Resources.Localize.Text("Reset Light Properties"), Capability.HasOverride))
        //{
        //    Capability.Reset();
        //}

        //
        // Body 

        var light = Capability.GameLight.GameLight != null ? Capability.GameLight.GameLight->RenderLight : null;
        if(light == null) return;

        if(Capability.SelectedLightType == -1)
        {
            switch(light->EmissionType)
            {
                case LightType.SpotLight:
                    Capability.SelectedLightType = 0;
                    break;
                case LightType.PointLight:
                    Capability.SelectedLightType = 1;
                    break;
                case LightType.FlatLight:
                    Capability.SelectedLightType = 2;
                    break;
                case LightType.WorldLight:
                    Capability.SelectedLightType = 3;
                    break;
            }
        }

        if(ImBrio.ButtonSelectorStrip("light_type", Vector2.Zero, ref Capability.SelectedLightType, ["Spot", "Point", "Flat", "World"]))
        {
            switch(Capability.SelectedLightType)
            {
                case 0:
                    light->EmissionType = LightType.SpotLight;
                    break;
                case 1:
                    light->EmissionType = LightType.PointLight;
                    break;
                case 2:
                    light->EmissionType = LightType.FlatLight;
                    break;
                case 3:
                    light->EmissionType = LightType.WorldLight;
                    break;
            }
        }

        switch(light->EmissionType)
        {
            case LightType.SpotLight:
                ImBrio.CenterNextElementWithPadding(15);
                ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###lightAngle"), ref light->SpotLightAngleDegrees, 0.0f, 180.0f, "%0.0f Degrees"u8);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Spot Light Angle"));

                ImBrio.CenterNextElementWithPadding(15);
                ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###lightSmothing"), ref light->AngularFalloffDegrees, 0.0f, 180.0f, "%0.0f Degrees"u8);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Spot Light Smothing"));
                break;

            case LightType.FlatLight:
                float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
                float padding = 10f;
                float full = ImGui.GetContentRegionAvail().X - ImGui.GetStyle().WindowPadding.X - padding;
                float half = (full - spacing) / 2;

                ImBrio.CenterNextElementWithPadding(15);
                using(ImRaii.ItemWidth(half))
                {
                    ImGui.SliderAngle("###lightAngle_x"u8, ref light->FlatLightSkewAngleDegrees.X, -90, 90);
                    ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Flat Light X"));

                    ImGui.SameLine(0, spacing);

                    ImGui.SliderAngle("###lightAngle_y"u8, ref light->FlatLightSkewAngleDegrees.Y, -90, 90);
                    ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Flat Light Y"));
                }

                ImBrio.CenterNextElementWithPadding(15);
                ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###lightAngleSlider"), ref light->AngularFalloffDegrees, 0.0f, 180.0f, "%0.0f Degrees"u8);
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Flat Light Falloff"));
                break;
        }

        // Falloff Power
        ImBrio.CenterNextElementWithPadding(15);
        ImGui.DragFloat(global::Brio.Resources.Localize.Text("###falloffPower"), ref light->FalloffFactor, 0.01f, 0.0f, 1000.0f);
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Light Falloff Factor Power"));

        // Range
        ImBrio.CenterNextElementWithPadding(15);
        if(ImGui.DragFloat(global::Brio.Resources.Localize.Text("###lightRange"), ref light->Range, 0.1f, 0, 900))
            Capability.GameLight.NeedsUpdate = true;
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Light Range"));

        //

        ImBrio.VerticalPadding(5);
        ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Color & Intensity"));

        var color = Vector3.SquareRoot(light->Color / 6);
        ImBrio.CenterNextElementWithPadding(15);
        if(ImGui.ColorEdit3(global::Brio.Resources.Localize.Text("###colorEdit3"), ref color, ImGuiColorEditFlags.Hdr))
        {
            light->Color = color * color * 6;
        }
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Light Color"));

        var intensity = light->Intensity;
        ImBrio.CenterNextElementWithPadding(15);
        if(ImGui.DragFloat(global::Brio.Resources.Localize.Text("###intensity"), ref intensity, 0.01f, 0.0f, 100.0f))
        {
            light->Intensity = intensity;
        }
        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Intensity"));

        //

        ImBrio.VerticalPadding(5);
        ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Shadows & Reflections"));

        var flag = light->LightFlags.HasFlag(LightFlags.Reflection);
        if(ImGui.Checkbox(global::Brio.Resources.Localize.Text("Enable Material Reflections"), ref flag))
        {
            light->LightFlags ^= LightFlags.Reflection;
        }

        bool[] bools =
        [
            light->LightFlags.HasFlag(LightFlags.CharaShadow),
            light->LightFlags.HasFlag(LightFlags.ObjectShadow),
            light->LightFlags.HasFlag(LightFlags.Dynamic),
        ];
        if(ImBrio.ToggleSelecterStrip("shadows_enable", Vector2.Zero, ref bools, ["Character", "Object", "Dynamic"], "Shadows"))
        {
            SetFlag(light, LightFlags.CharaShadow, bools[0]);
            SetFlag(light, LightFlags.ObjectShadow, bools[1]);
            SetFlag(light, LightFlags.Dynamic, bools[2]);

            static void SetFlag(LightRenderObject* light, LightFlags flag, bool enabled)
            {
                if(enabled) light->LightFlags |= flag;
                else light->LightFlags &= ~flag;
            }
        }
    }

    public static unsafe void DrawLightTransformHeader(LightTransformCapability Capability)
    {
        var overlayOpen = Capability.OverlayOpen;
        if(ImBrio.FontIconButton($"overlay_{Capability.Entity.Id}", overlayOpen ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye, overlayOpen ? "Close Overlay" : "Open Overlay"))
        {
            Capability.OverlayOpen = !overlayOpen;
        }

        ImBrio.VerticalSeparator(24);

        if(ImBrio.ToggelFontIconButton($"save_{Capability.Entity.Id}", FontAwesomeIcon.BookBookmark, new Vector2(25, 0), false, tooltip: global::Brio.Resources.Localize.Text("Light Presets")))
        {
            ImGui.OpenPopup($"DrawPresetPopup");
        }

        FileUIHelpers.DrawPresetPopup(PresetType.Light, Capability.Entity);

        ImBrio.VerticalSeparator(24);

        if(ImBrio.FontIconButton($"undo_{Capability.Entity.Id}", FontAwesomeIcon.Reply, "Undo", Capability.CanUndo) || (InputManagerService.ActionKeysPressedLastFrame(InputAction.Posing_Undo) && Capability.CanUndo))
        {
            Capability.Undo();
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButton($"redo_{Capability.Entity.Id}", FontAwesomeIcon.Share, "Redo", Capability.CanRedo) || (InputManagerService.ActionKeysPressedLastFrame(InputAction.Posing_Redo) && Capability.CanRedo))
        {
            Capability.Redo();
        }

        ImBrio.VerticalSeparator(24);

        if(ImBrio.ToggelFontIconButton($"###togglegizmo_{Capability.Entity.Id}", FontAwesomeIcon.CompressArrowsAlt, Vector2.Zero, Capability.IsAdvancedGismoVisible, tooltip: Capability.IsAdvancedGismoVisible ? "Disable Advanced Gizmo" : "Enable Advanced Gizmo"))
        {
            Capability.IsAdvancedGismoVisible = !Capability.IsAdvancedGismoVisible;
        }

        ImGui.SameLine();

        if(ImBrio.ToggelFontIconButton("togglelight", FontAwesomeIcon.Lightbulb, Vector2.Zero, Capability.GameLight.IsVisible, tooltip: Capability.GameLight.IsVisible ? "Turn Light Off" : "Turn Light On"))
        {
            Capability.GameLight.ToggleLight();
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButtonRight($"reset_{Capability.Entity.Id}", FontAwesomeIcon.Undo, 1, "Reset Light Transform", Capability.HasOverride))
        {
            Capability.Reset();
        }
    }
}
