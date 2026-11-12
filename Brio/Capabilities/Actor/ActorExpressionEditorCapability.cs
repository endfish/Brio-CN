using Brio.Capabilities.Core;
using Brio.Capabilities.Posing;
using Brio.Entities.Actor;
using Brio.Game.GPose;
using Brio.UI;
using Brio.UI.Controls.Stateless;
using Brio.UI.Widgets.Core;
using Dalamud.Interface;

namespace Brio.Capabilities.Actor;

// Keep a Face Control entry for poseable actors that do not get the
// player-only expression picker. This widget never enables dynamic look-at.
internal sealed class ActorExpressionEditorCapability : Capability
{
    public ActorExpressionEditorCapability(ActorEntity actor, GPoseService gpose) : base(actor)
        => Widget = new Entry(this, gpose);

    private sealed class Entry(ActorExpressionEditorCapability capability, GPoseService gpose)
        : Widget<ActorExpressionEditorCapability>(capability)
    {
        public override string HeaderName => "Face Control";

        public override WidgetFlags Flags => gpose.IsGPosing
            && (Capability.Entity.TryGetCapability<ActorAppearanceCapability>(out var appearance) && appearance.IsHuman
                || Capability.Entity.TryGetCapability<SkeletonPosingCapability>(out var skeleton) && skeleton.CharacterIsDawntrail)
            ? WidgetFlags.DrawBody
            : WidgetFlags.None;

        public override void DrawBody()
        {
            if(ImBrio.Button("Expression Editor", FontAwesomeIcon.Edit))
                UIManager.Instance.ToggleExpressionEditorWindow();
        }
    }
}
