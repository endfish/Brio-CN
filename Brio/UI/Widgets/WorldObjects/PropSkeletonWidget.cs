using Brio.Capabilities.WorldObjects;
using Brio.Core;
using Brio.Resources;
using Brio.UI.Controls.Stateless;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Linq;
using System.Numerics;

namespace Brio.UI.Widgets.WorldObjects;

public class PropSkeletonWidget(PropSkeletonCapability capability) : Widget<PropSkeletonCapability>(capability)
{
    public override string HeaderName => "Prop Bones";
    public override WidgetFlags Flags => WidgetFlags.DrawBody | WidgetFlags.DefaultOpen | WidgetFlags.CanHide;
    private string _search = string.Empty;
    private string? _selectedKey;
    private Vector3? _editingEuler;

    public override void DrawBody()
    {
        // Loading, selection and help text must not resize the parent window.
        var height = ImGui.GetFrameHeightWithSpacing() * 8 + ImGui.GetTextLineHeightWithSpacing() * 8;
        using var body = ImRaii.Child("prop_bone_editor", new Vector2(-1, height), false,
            ImGuiWindowFlags.AlwaysVerticalScrollbar);
        if(body.Success)
            DrawEditor();
    }

    private void DrawEditor()
    {
        if(!Capability.IsReady)
        {
            ImGui.TextWrapped(Localize.Text("No editable prop skeleton is loaded yet."));
            return;
        }

        if(ImBrio.FontIconButton("undo_prop_bone", FontAwesomeIcon.Reply, Localize.Text("Undo bone edit"), Capability.CanUndo))
            Capability.Undo();
        ImGui.SameLine();
        if(ImBrio.FontIconButton("redo_prop_bone", FontAwesomeIcon.Share, Localize.Text("Redo bone edit"), Capability.CanRedo))
            Capability.Redo();
        ImGui.SameLine();
        if(ImBrio.FontIconButton("reset_prop_bones", FontAwesomeIcon.Undo, Localize.Text("Reset all prop bones"), Capability.HasOverrides))
            Capability.ResetAll();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###prop_bone_search", Localize.Text("Search bone name"), ref _search, 128);
        using(var list = ImRaii.Child("prop_bone_list", new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 7), true))
        {
            if(list.Success)
            {
                var found = false;
                foreach(var bone in Capability.Bones)
                {
                    if(!bone.Description.Contains(_search, StringComparison.OrdinalIgnoreCase))
                        continue;

                    found = true;
                    if(ImGui.Selectable($"{bone.DisplayName}###prop_bone_{bone.Key}", _selectedKey == bone.Key))
                    {
                        Capability.CommitEdit();
                        _selectedKey = bone.Key;
                        _editingEuler = null;
                    }
                    if(ImGui.IsItemHovered())
                        ImGui.SetTooltip(Localize.Format("Bone identifier: {0}", bone.Description));
                }
                if(!found)
                    ImGui.TextDisabled(Localize.Text("No matching bones."));
            }
        }

        var selected = Capability.Bones.FirstOrDefault(bone => bone.Key == _selectedKey);
        if(selected is null)
        {
            ImGui.TextWrapped(Localize.Text("Select a bone to adjust its position, rotation, and scale."));
            return;
        }

        ImGui.TextUnformatted(selected.DisplayName);
        var transform = selected.Current;
        var euler = _editingEuler ?? transform.Rotation.ToEuler();
        var (positionChanged, positionActive) = ImBrio.DragFloat3("###prop_bone_position", ref transform.Position, 0.01f,
            FontAwesomeIcon.ArrowsUpDownLeftRight, "Position");
        var (rotationChanged, rotationActive) = ImBrio.DragFloat3("###prop_bone_rotation", ref euler, 1f,
            FontAwesomeIcon.ArrowsSpin, "Rotation");
        var (scaleChanged, scaleActive) = ImBrio.DragFloat3("###prop_bone_scale", ref transform.Scale, 0.01f,
            FontAwesomeIcon.ExpandAlt, "Scale");

        if(positionChanged || rotationChanged || scaleChanged)
        {
            transform.Rotation = euler.ToQuaternion();
            Capability.SetTransform(selected, transform);
        }

        if(positionActive || rotationActive || scaleActive)
            _editingEuler = euler;
        else
        {
            _editingEuler = null;
            Capability.CommitEdit();
        }

        if(ImGui.Button(Localize.Text("Reset selected bone")))
            Capability.ResetBone(selected);

        ImGui.TextWrapped(Localize.Text("Bone edits are saved with the scene and copied when cloning. Scaling a part's independent bone close to zero can hide that part; it cannot reveal meshes hidden by the model variant."));
    }
}
