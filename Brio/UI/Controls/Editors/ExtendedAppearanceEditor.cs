using Brio.Game.Actor.Appearance;
using Brio.UI.Controls.Stateless;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Brio.UI.Controls.Editors;

public class ExtendedAppearanceEditor
{
    private static float MaxItemWidth => ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("XXXXXXXXXX").X;
    private static float LabelStart => MaxItemWidth + ImGui.GetCursorPosX() + ImGui.GetStyle().FramePadding.X * 2f;

    public bool Draw(ref ActorAppearance currentAppearance, ActorAppearance originalAppearance, bool canTint)
    {
        bool didChange = false;

        didChange |= DrawReset(ref currentAppearance, originalAppearance);
        didChange |= DrawTransparency(ref currentAppearance);
        didChange |= DrawWetness(ref currentAppearance);
        didChange |= DrawTints(ref currentAppearance, canTint);

        return didChange;
    }

    private bool DrawReset(ref ActorAppearance currentAppearance, ActorAppearance originalAppearance)
    {
        bool didChange = false;

        var resetTo = ImGui.GetCursorPos();
        bool extendedChanged = !currentAppearance.ExtendedAppearance.Equals(originalAppearance.ExtendedAppearance);
        if(ImBrio.FontIconButtonRight("reset_extended", FontAwesomeIcon.Undo, 1, global::Brio.Resources.Localize.Text("Reset Extended"), extendedChanged))
        {
            currentAppearance.ExtendedAppearance = originalAppearance.ExtendedAppearance;
            didChange |= true;
        }
        ImGui.SetCursorPos(resetTo);

        return didChange;
    }

    private bool DrawTransparency(ref ActorAppearance appearance)
    {
        bool didChange = false;

        float transparency = appearance.ExtendedAppearance.Transparency;

        ImGui.SetNextItemWidth(MaxItemWidth);
        if(ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###transparency"), ref transparency, 0.0f, 1.0f, "%.2f"))
        {
            appearance.ExtendedAppearance.Transparency = transparency;
            didChange = true;
        }
        ImGui.SameLine();
        ImGui.SetCursorPosX(LabelStart);
        ImGui.Text(global::Brio.Resources.Localize.Text("Alpha"));

        return didChange;
    }

    private bool DrawWetness(ref ActorAppearance appearance)
    {
        bool didChange = false;

        float wetness = appearance.ExtendedAppearance.Wetness;

        ImGui.SetNextItemWidth(MaxItemWidth);
        if(ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###wetness"), ref wetness, 0.0f, 1.0f, "%.2f"))
        {
            appearance.ExtendedAppearance.Wetness = wetness;
            didChange = true;
        }
        ImGui.SameLine();
        ImGui.SetCursorPosX(LabelStart);
        ImGui.Text(global::Brio.Resources.Localize.Text("Wet"));

        wetness = appearance.ExtendedAppearance.WetnessDepth;
        ImGui.SetNextItemWidth(MaxItemWidth);
        if(ImGui.SliderFloat(global::Brio.Resources.Localize.Text("###wetnessdepth"), ref wetness, 0.0f, 3.0f, "%.2f"))
        {
            appearance.ExtendedAppearance.WetnessDepth = wetness;
            didChange = true;
        }
        ImGui.SameLine();
        ImGui.SetCursorPosX(LabelStart);
        ImGui.Text(global::Brio.Resources.Localize.Text("Wet Depth"));

        return didChange;
    }




    private bool DrawTints(ref ActorAppearance appearance, bool canTint)
    {
        bool didChange = false;

        using(ImRaii.Disabled(!canTint))
        {
            didChange |= DrawTint(ref appearance.ExtendedAppearance.CharacterTint, "character", "Character");
            ImGui.SameLine();
            didChange |= DrawTint(ref appearance.ExtendedAppearance.MainHandTint, "mainhand", "Main Hand");
            ImGui.SameLine();
            didChange |= DrawTint(ref appearance.ExtendedAppearance.OffHandTint, "offhand", "Off Hand");
            ImGui.SameLine();
            ImGui.SetCursorPosX(LabelStart);
            ImGui.Text(global::Brio.Resources.Localize.Text("Tints"));
        }

        return didChange;
    }

    private bool DrawTint(ref Vector4 tint, string id, string label)
    {
        using(ImRaii.PushId($"tint_{id}"))
        {
            bool didChange = false;

            var tempTint = tint;
            if(ImGui.ColorButton($"{label}###{id}", tempTint, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.NoAlpha))
            {
                ImGui.OpenPopup($"{id}_tint_popup");
            }

            using(var popup = ImRaii.Popup($"{id}_tint_popup"))
            {
                if(popup.Success)
                {
                    if(ImGui.ColorPicker4(global::Brio.Resources.Localize.Text("###tint"), ref tempTint))
                    {
                        tint = tempTint;
                        didChange = true;
                    }
                }
            }

            return didChange;
        }
    }
}



