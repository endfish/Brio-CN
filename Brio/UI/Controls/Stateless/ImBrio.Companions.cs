using System.Numerics;
using Brio.Game.Types;
using Brio.Resources;
using Dalamud.Bindings.ImGui;

namespace Brio.UI.Controls.Stateless;

public static partial class ImBrio
{
    public static bool BorderedGameIcon(string id, CompanionRowUnion union, bool showText = true, ImGuiButtonFlags flags = ImGuiButtonFlags.MouseButtonLeft, Vector2? size = null)
    {
        var (description, icon) = union.Match(
           companion => (Localize.Format("{0}\n{1}\nModel: {2}", GameDataProvider.Instance.GetCompanionName(companion.RowId), companion.RowId, companion.Model.RowId), companion.Icon),
           mount => (Localize.Format("{0}\n{1}\nModel: {2}", GameDataProvider.Instance.GetMountName(mount.RowId), mount.RowId, mount.ModelChara.RowId), mount.Icon),
           ornament => (Localize.Format("{0}\n{1}\nModel: {2}", GameDataProvider.Instance.GetOrnamentName(ornament.RowId), ornament.RowId, ornament.Model), ornament.Icon),
           none => (global::Brio.Resources.Localize.Text("None"), (uint)0)
       );

        bool wasClicked = false;

        if(!showText)
        {
            description = string.Empty;
        }

        var placeholderIcon = union.Match(
                companion => "Images.Companion.png",
                mount => "Images.Mount.png",
                ornament => "Images.Ornament.png",
                none => "Images.Companion.png"
            );

        wasClicked = BorderedGameIcon(id, icon, placeholderIcon, description, flags, size);

        return wasClicked;
    }
}
