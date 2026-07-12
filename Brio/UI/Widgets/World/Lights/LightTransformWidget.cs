using Brio.Capabilities.World;
using Brio.UI.Controls.Editors;
using Brio.UI.Controls.Stateless;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;

namespace Brio.UI.Widgets.World.Lights;

public class LightTransformWidget(LightTransformCapability lightGizmoCapability) : Widget<LightTransformCapability>(lightGizmoCapability)
{
    public override string HeaderName => "Light Editor";

    public override WidgetFlags Flags => WidgetFlags.DrawBody | WidgetFlags.DefaultOpen | WidgetFlags.CanHide | WidgetFlags.HasAdvanced;

    private readonly ITransformableEditor _transformableEditor = new();

    public override void ToggleAdvancedWindow()
    {
        Capability.LightWindowOpen = !Capability.LightWindowOpen;
    }

    public override void DrawBody()
    {
        LightEditor.DrawLightTransformHeader(Capability);

        ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Light Transform"));

        _transformableEditor.Draw($"light_transform_{Capability.Entity.Id}", Capability.Light, 0.1f);

        var lightRenderingCapability = Capability.Light.GetCapability<LightRenderingCapability>();

        ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Light Properties"));

        LightEditor.DrawLightProperties(lightRenderingCapability);

        if(ImGui.CollapsingHeader(global::Brio.Resources.Localize.Text("Advanced Settings"), ImGuiTreeNodeFlags.None))
        {
            LightEditor.DrawAdvancedShadows(lightRenderingCapability);
        }
    }
}
