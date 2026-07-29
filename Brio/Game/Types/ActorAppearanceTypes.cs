using Brio.Game.Actor.Appearance;
using Lumina.Excel.Sheets;
using OneOf;
using OneOf.Types;
using Companion = Lumina.Excel.Sheets.Companion;
using Ornament = Lumina.Excel.Sheets.Ornament;

namespace Brio.Game.Types;

[GenerateOneOf]
public partial class ActorAppearanceUnion : OneOfBase<BNpcBase, ENpcBase, Mount, Companion, Ornament, Transformation, None>
{
    public static implicit operator ActorAppearance(ActorAppearanceUnion union) => union.Match(
        bnpc => ActorAppearance.FromBNpc(bnpc),
        enpc => ActorAppearance.FromENpc(enpc),
        mount => ActorAppearance.FromModelChara((int)mount.ModelChara.RowId),
        companion => ActorAppearance.FromModelChara((int)companion.Model.RowId),
        ornament => ActorAppearance.FromModelChara(ornament.Model),
        transformation => ActorAppearance.FromTransformation(transformation),
        none => new ActorAppearance()
    );
}

