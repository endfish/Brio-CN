using Brio.Game.Types;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Brio.UI.Controls.Stateless;

public static partial class ImBrio
{
    public static bool BorderedWeatherGameIcon(string id, WeatherUnion union, bool showText = true, ImGuiButtonFlags flags = ImGuiButtonFlags.MouseButtonLeft, Vector2? size = null)
    {
        var (description, icon) = union.Match(
           weather => (global::Brio.Resources.Localize.Format("{0}\n{1}\nType: {2}", weather.Name, weather.RowId, weather.Description), (uint)weather.Icon),
           none => (global::Brio.Resources.Localize.Text("None"), (byte)0)
        );

        if(!showText)
        {
            description = string.Empty;
        }

        return BorderedGameIcon(id, icon, "Images.Weather.png", description, flags, size);
    }
}
